use ra_ap_base_db::{
    AbsPathBuf, CrateGraphBuilder, CrateName, CrateOrigin, CrateWorkspaceData, DependencyBuilder,
    Env, FileId, FileSet, LangCrateOrigin, SourceRoot, VfsPath,
};
use ra_ap_cfg::CfgOptions;
use ra_ap_hir::{self as hir, ChangeWithProcMacros, HasSource, HirDisplay, Semantics};
use ra_ap_ide_db::RootDatabase;
use ra_ap_intern::Symbol;
use ra_ap_span::Edition;
use ra_ap_syntax::{
    AstNode, SyntaxKind, SyntaxNode,
    ast::{self, HasName},
};
use serde::Deserialize;
use serde_json::{Value, json};
use std::{
    collections::HashMap,
    io::{self, BufRead, Write},
};
use triomphe::Arc;
mod project;

#[derive(Deserialize)]
struct File {
    path: String,
    text: String,
    #[serde(default)]
    library: bool,
}
#[derive(Deserialize)]
struct Dependency {
    name: String,
    #[serde(rename = "crate")]
    crate_index: usize,
}
#[derive(Deserialize)]
struct Crate {
    root: String,
    name: String,
    edition: String,
    #[serde(default)]
    deps: Vec<Dependency>,
    #[serde(default)]
    cfg: Vec<String>,
    #[serde(default)]
    sysroot: bool,
}
#[derive(Deserialize)]
struct Request {
    op: String,
    #[serde(default)]
    text: String,
    #[serde(default)]
    file: String,
    #[serde(default)]
    context: String,
    #[serde(default)]
    files: Vec<File>,
    #[serde(default)]
    crates: Vec<Crate>,
    #[serde(default)]
    root: String,
    #[serde(default)]
    cargo_home: String,
    #[serde(default)]
    cfg: Vec<String>,
    #[serde(default)]
    target: String,
    #[serde(default)]
    stdlib: String,
    #[serde(default)]
    expected_text: Option<String>,
}
struct Workspace {
    db: RootDatabase,
    files: Vec<File>,
    ids: HashMap<String, FileId>,
}

impl Workspace {
    fn new(files: Vec<File>, crates: Vec<Crate>) -> Result<Self, String> {
        let mut db = RootDatabase::default();
        let mut change = ChangeWithProcMacros::default();
        let mut local = FileSet::default();
        let mut library = FileSet::default();
        let mut ids = HashMap::new();
        for (index, file) in files.iter().enumerate() {
            let id = FileId::from_raw(index as u32);
            ids.insert(file.path.clone(), id);
            let virtual_path = format!("/{}", file.path.replace('\\', "/").trim_start_matches('/'));
            let set = if file.library { &mut library } else { &mut local };
            set.insert(id, VfsPath::new_virtual_path(virtual_path));
            change.change_file(id, Some(file.text.clone()));
        }
        change.set_roots(vec![
            SourceRoot::new_local(local),
            SourceRoot::new_library(library),
        ]);
        let mut graph = CrateGraphBuilder::default();
        let mut crate_ids = Vec::new();
        for krate in &crates {
            let root = *ids
                .get(&krate.root)
                .ok_or("crate root is not in source snapshot")?;
            let edition = match krate.edition.as_str() {
                "2015" => Edition::Edition2015,
                "2018" => Edition::Edition2018,
                "2021" => Edition::Edition2021,
                "2024" => Edition::Edition2024,
                _ => return Err(format!("unsupported Rust edition {}", krate.edition)),
            };
            let mut cfg = CfgOptions::default();
            for entry in &krate.cfg {
                if let Some((key, value)) = entry.split_once('=') {
                    cfg.insert_key_value(
                        Symbol::intern(key),
                        Symbol::intern(value.trim_matches('"')),
                    );
                } else {
                    cfg.insert_atom(Symbol::intern(entry));
                }
            }
            let current = std::env::current_dir().map_err(|e| e.to_string())?;
            let cwd = Arc::new(
                AbsPathBuf::try_from(current.to_str().ok_or("non-UTF8 working directory")?)
                    .map_err(|e| format!("{e:?}"))?,
            );
            crate_ids.push(graph.add_crate_root(
                root,
                edition,
                Some(CrateName::new(&krate.name)?.into()),
                None,
                cfg,
                None,
                Env::default(),
                if krate.sysroot {
                    CrateOrigin::Lang(LangCrateOrigin::from(krate.name.as_str()))
                } else {
                    CrateOrigin::Local {
                        repo: None,
                        name: Some(Symbol::intern(&krate.name)),
                    }
                },
                vec![],
                false,
                cwd,
                Arc::new(CrateWorkspaceData {
                    target: Err(
                        "target layout unavailable; no project compiler is executed".into(),
                    ),
                    toolchain: None,
                }),
            ));
        }
        for (index, krate) in crates.iter().enumerate() {
            for dep in &krate.deps {
                let target = *crate_ids
                    .get(dep.crate_index)
                    .ok_or("dependency index outside graph")?;
                let name = CrateName::new(&dep.name).map_err(|e| format!("{e:?}"))?;
                graph
                    .add_dep(
                        crate_ids[index],
                        DependencyBuilder::with_prelude(
                            name,
                            target,
                            !(crates[dep.crate_index].sysroot
                                && dep.name == "alloc"
                                && !krate.sysroot),
                            crates[dep.crate_index].sysroot,
                        ),
                    )
                    .map_err(|e| e.to_string())?;
            }
        }
        change.set_crate_graph(graph);
        db.apply_change(change);
        Ok(Self { db, files, ids })
    }

    fn analyze(&self, path: &str) -> Result<Value, String> {
        let id = *self
            .ids
            .get(path)
            .ok_or("requested file is not in source snapshot")?;
        let sema = Semantics::new(&self.db);
        let tree = sema.parse_guess_edition(id);
        let mut facts = Vec::new();
        for node in tree.syntax().descendants() {
            let Some(expr) = ast::Expr::cast(node.clone()) else {
                continue;
            };
            let scope = sema.scope(&node);
            let Some(scope) = scope else { continue };
            let target = scope.module().krate(&self.db).to_display_target(&self.db);
            let type_text = |ty: hir::Type<'_>| {
                (!ty.contains_unknown()).then(|| ty.display(&self.db, target).to_string())
            };
            let value_type = sema
                .type_of_expr(&expr)
                .and_then(|it| type_text(it.original));
            let mut receiver_type = None;
            let mut function = None;
            if let Some(call) = ast::CallExpr::cast(node.clone()) {
                function = call
                    .expr()
                    .and_then(|e| sema.resolve_expr_as_callable(&e))
                    .and_then(|c| c.as_function());
            } else if let Some(call) = ast::MethodCallExpr::cast(node.clone()) {
                receiver_type = call
                    .receiver()
                    .and_then(|e| sema.type_of_expr(&e))
                    .and_then(|it| type_text(it.original));
                function = sema.resolve_method_call(&call);
            }
            let callable = function.map(|function| {
                let source = function.source(&self.db);
                let mut source_path = None;
                let mut start = None;
                let mut end = None;
                let mut declaration_span = None;
                let mut declaring_type = None;
                let mut signature = function.ty(&self.db).display(&self.db, target).to_string();
                if let Some(source) = source {
                    if let Some(file_id) = source.file_id.file_id() {
                        if let Some(file) = self.files.get(file_id.file_id(&self.db).index() as usize) {
                            source_path = Some(file.path.clone());
                            let (line, column) = position(&file.text, source_start(source.value.syntax()) as usize);
                            let (end_line, end_column) = position(&file.text, source.value.syntax().text_range().end().into());
                            declaration_span = Some(json!({"line":line,"column":column,"end_line":end_line,"end_column":end_column}));
                        }
                    }
                    let syntax = source.value.syntax();
                    start = Some(source_start(syntax));
                    end = Some(u32::from(syntax.text_range().end()));
                    let header_end = source.value.body().map(|b| b.syntax().text_range().start())
                        .unwrap_or(syntax.text_range().end());
                    let header_start = syntax.children_with_tokens()
                        .find(|part| !matches!(part.kind(), SyntaxKind::ATTR | SyntaxKind::DOC_COMMENT | SyntaxKind::COMMENT | SyntaxKind::WHITESPACE))
                        .map(|part| part.text_range().start()).unwrap_or(syntax.text_range().start());
                    signature = syntax.text().slice(header_start - syntax.text_range().start()..header_end - syntax.text_range().start()).to_string().trim().trim_end_matches(';').to_owned();
                    for parent in syntax.ancestors().skip(1) {
                        if let Some(implementation) = ast::Impl::cast(parent.clone()) {
                            declaring_type = implementation.self_ty().map(|t| t.syntax().text().to_string());
                            break;
                        }
                        if let Some(trait_) = ast::Trait::cast(parent) {
                            declaring_type = trait_.name().map(|n| n.text().to_string());
                            break;
                        }
                    }
                }
                let module = function.module(&self.db);
                let mut modules = module.path_to_root(&self.db);
                modules.reverse();
                let namespace = std::iter::once(module.krate(&self.db).display_name(&self.db).map(|n| n.to_string()))
                    .chain(modules.iter().map(|m| m.name(&self.db).map(|n| n.display(&self.db, Edition::CURRENT).to_string())))
                    .flatten().collect::<Vec<_>>().join("::");
                json!({"name":function.name(&self.db).display(&self.db, Edition::CURRENT).to_string(),
                    "signature":signature, "declaring_type":declaring_type, "namespace":namespace,
                    "return_type":type_text(function.ret_type(&self.db)),
                    "parameters":function.params_without_self(&self.db).into_iter().map(|p| type_text(p.ty().clone())).collect::<Vec<_>>(),
                    "source_file":source_path, "start":start, "end":end, "span":declaration_span})
            });
            facts.push(json!({"start":u32::from(node.text_range().start()), "end":u32::from(node.text_range().end()),
                "kind":format!("{:?}", node.kind()), "value_type":value_type,
                "receiver_type":receiver_type, "callable":callable}));
        }
        Ok(json!({"facts":facts}))
    }
}

fn position(text: &str, offset: usize) -> (usize, usize) {
    let prefix = &text[..offset];
    let line = prefix.bytes().filter(|b| *b == b'\n').count() + 1;
    let column = prefix
        .rsplit_once('\n')
        .map_or(prefix, |(_, tail)| tail)
        .encode_utf16()
        .count()
        + 1;
    (line, column)
}

fn source_start(node: &SyntaxNode) -> u32 {
    node.children_with_tokens()
        .find(|part| !part.kind().is_trivia() && part.kind() != SyntaxKind::DOC_COMMENT)
        .map(|part| u32::from(part.text_range().start()))
        .unwrap_or_else(|| u32::from(node.text_range().start()))
}

fn syntax(text: &str) -> Value {
    let parsed = ast::SourceFile::parse(text, Edition::CURRENT);
    let root = parsed.tree().syntax().clone();
    let mut nodes = Vec::new();
    let mut parents: HashMap<SyntaxNode, usize> = HashMap::new();
    for node in root.descendants() {
        let parent = node.parent().and_then(|p| parents.get(&p).copied());
        let index = nodes.len();
        parents.insert(node.clone(), index);
        let tokens = node
            .children_with_tokens()
            .filter_map(|element| element.into_token())
            .filter(|token| !token.kind().is_trivia())
            .map(|token| token.text().to_owned())
            .collect::<Vec<_>>();
        nodes.push(
            json!({"kind":format!("{:?}",node.kind()),"start":source_start(&node),
            "end":u32::from(node.text_range().end()),"parent":parent,"tokens":tokens}),
        );
    }
    let errors = parsed
        .errors()
        .iter()
        .map(|e| {
            json!({"message":e.to_string(),
        "start":u32::from(e.range().start()),"end":u32::from(e.range().end())})
        })
        .collect::<Vec<_>>();
    let comments = root.descendants_with_tokens().filter_map(|element| element.into_token())
        .filter(|token| token.kind() == ra_ap_syntax::SyntaxKind::COMMENT)
        .map(|token| json!({"start":u32::from(token.text_range().start()),"end":u32::from(token.text_range().end())}))
        .collect::<Vec<_>>();
    json!({"nodes":nodes, "errors":errors, "comments":comments})
}

fn run(request: Request, workspace: &mut Option<(String, Workspace)>) -> Result<Value, String> {
    match request.op.as_str() {
        "version" => Ok(json!({"backend":"rust-analyzer","version":"0.0.358","protocol":1})),
        "toolchain" => match project::requested_toolchain(&request.file) {
            Ok(channel) => Ok(json!({"channel":channel, "unsupported":null})),
            Err(error) => Ok(json!({"channel":null,"unsupported":error})),
        },
        "parse" => Ok(syntax(&request.text)),
        "analyze" => {
            if workspace.as_ref().map(|(key, _)| key) != Some(&request.context) {
                *workspace = Some((
                    request.context.clone(),
                    Workspace::new(request.files, request.crates)?,
                ));
            }
            workspace.as_ref().unwrap().1.analyze(&request.file)
        }
        "project" => {
            let snapshot = project::Snapshot::load(
                &request.root,
                &request.file,
                &request.cargo_home,
                request.cfg,
                request.target,
                &request.stdlib,
            )?;
            if request.expected_text.as_ref().is_some_and(|expected| {
                snapshot
                    .files
                    .iter()
                    .find(|f| f.path == request.file)
                    .is_none_or(|f| &f.text != expected)
            }) {
                return Err("Rust source changed since its syntax parse; requery before requesting semantic facts.".into());
            }
            if !snapshot.allow_binding {
                return Ok(json!({"facts":[],"diagnostics":snapshot.diagnostics}));
            }
            let key = snapshot.identity();
            if workspace.as_ref().map(|(key, _)| key) != Some(&key) {
                *workspace = Some((key, Workspace::new(snapshot.files, snapshot.crates)?));
            }
            let mut response = workspace.as_ref().unwrap().1.analyze(&request.file)?;
            response["diagnostics"] = json!(snapshot.diagnostics);
            Ok(response)
        }
        _ => Err("unknown frontend operation".to_owned()),
    }
}

fn main() {
    let mut workspace = None;
    for line in io::stdin().lock().lines() {
        let line = match line {
            Ok(line) => line,
            Err(error) => {
                eprintln!("{error}");
                std::process::exit(1);
            }
        };
        let result = serde_json::from_str::<Request>(&line)
            .map_err(|e| e.to_string())
            .and_then(|request| run(request, &mut workspace));
        match result {
            Ok(mut response) => {
                response["backend"] = json!("rust-analyzer");
                response["version"] = json!("0.0.358");
                response["protocol"] = json!(1);
                println!("{response}");
            }
            Err(error) => println!("{}", serde_json::json!({"error": error.to_string()})),
        }
        io::stdout().flush().expect("stdout");
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn project_ownership_does_not_consider_unrelated_sysroot_crates() {
        let request: Request = serde_json::from_value(json!({
            "op": "analyze",
            "files": [
                {"path": "/app/lib.rs", "text": "pub struct Alpha; impl Alpha { pub fn get(&self, value: u32) -> u32 { value } } pub fn run() { let a = Alpha; a.get(1); }"},
                {"path": "/sysroot/core/lib.rs", "text": "#![no_core]", "library": true},
                {"path": "/sysroot/std/lib.rs", "text": "#![no_core]", "library": true}
            ],
            "crates": [
                {"root": "/app/lib.rs", "name": "app", "edition": "2024"},
                {"root": "/sysroot/core/lib.rs", "name": "core", "edition": "2024", "sysroot": true},
                {"root": "/sysroot/std/lib.rs", "name": "std", "edition": "2024", "sysroot": true}
            ]
        })).unwrap();
        let workspace = Workspace::new(request.files, request.crates).unwrap();
        assert_eq!(
            ra_ap_base_db::relevant_crates(&workspace.db, workspace.ids["/app/lib.rs"]).len(),
            1,
            "project edition/module discovery must not resolve unrelated sysroot crate ownership"
        );
        assert_eq!(
            ra_ap_base_db::relevant_crates(&workspace.db, workspace.ids["/sysroot/core/lib.rs"]).len(),
            2,
            "sysroot source roots remain available for maintained semantic resolution"
        );
        let result = workspace.analyze("/app/lib.rs").unwrap();
        let call = result["facts"].as_array().unwrap().iter()
            .find(|fact| fact["callable"]["name"] == "get").unwrap();
        assert_eq!(call["receiver_type"], "Alpha");
        assert_eq!(call["value_type"], "u32");
        assert_eq!(call["callable"]["signature"], "pub fn get(&self, value: u32) -> u32");
        assert_eq!(call["callable"]["source_file"], "/app/lib.rs");
    }
}
