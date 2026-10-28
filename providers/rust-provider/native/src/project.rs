use crate::{Crate, Dependency, File};
use cargo_platform::Platform;
use ra_ap_span::Edition;
use ra_ap_syntax::{AstNode, ast};
use semver::{Version, VersionReq};
use std::{
    collections::{BTreeMap, HashSet},
    fs,
    hash::{Hash, Hasher},
    path::{Path, PathBuf},
};
use toml::Value;

pub struct Snapshot {
    pub files: Vec<File>,
    pub crates: Vec<Crate>,
    pub diagnostics: Vec<String>,
    pub allow_binding: bool,
}

struct Loader {
    snapshot: Snapshot,
    ids: BTreeMap<PathBuf, usize>,
    loaded_files: HashSet<PathBuf>,
    cargo_home: PathBuf,
    lock: Vec<Value>,
    cfg: Vec<String>,
    target: String,
    bytes: usize,
    requested: BTreeMap<PathBuf, HashSet<String>>,
}

fn read(path: &Path) -> Result<Value, String> {
    toml::from_str(&fs::read_to_string(path).map_err(|e| format!("{}: {e}", path.display()))?)
        .map_err(|e| format!("{}: {e}", path.display()))
}
fn string<'a>(value: &'a Value, name: &str) -> Option<&'a str> {
    value.get(name)?.as_str()
}
fn workspace_for(directory: &Path, manifest: &Value) -> Result<Option<(PathBuf, Value)>, String> {
    if let Some(relative) = manifest.get("package").and_then(|p| string(p, "workspace")) {
        let path = directory.join(relative).join("Cargo.toml");
        return Ok(Some((path.clone(), read(&path)?)));
    }
    for ancestor in directory.ancestors() {
        let path = ancestor.join("Cargo.toml");
        if path.is_file() {
            let value = read(&path)?;
            if value.get("workspace").is_some() {
                return Ok(Some((path, value)));
            }
        }
    }
    Ok(None)
}
pub fn path_string(path: &Path) -> String {
    let path = path.to_string_lossy();
    if let Some(unc) = path.strip_prefix(r"\\?\UNC\") {
        format!(r"\\{unc}")
    } else {
        path.trim_start_matches(r"\\?\").to_string()
    }
}

pub fn requested_toolchain(file: &str) -> Result<Option<String>, String> {
    for directory in Path::new(file).ancestors().skip(1) {
        for name in ["rust-toolchain.toml", "rust-toolchain"] {
            let path = directory.join(name);
            if !path.is_file() {
                continue;
            }
            let text = fs::read_to_string(&path).map_err(|e| e.to_string())?;
            let channel = if name.ends_with(".toml") {
                let parsed: Value = toml::from_str(&text).map_err(|e| e.to_string())?;
                parsed
                    .get("toolchain")
                    .and_then(|t| string(t, "channel"))
                    .map(str::to_owned)
            } else {
                Some(text.trim().to_owned())
            };
            let channel = channel
                .ok_or("Custom path Rust toolchain is unsupported; semantic context is unknown.")?;
            if channel.is_empty()
                || channel.len() > 128
                || !channel
                    .chars()
                    .all(|c| c.is_ascii_alphanumeric() || matches!(c, '-' | '_' | '.'))
                || channel.starts_with('.')
            {
                return Err("Unsupported Rust toolchain channel; no project toolchain executable is launched.".into());
            }
            return Ok(Some(channel));
        }
    }
    Ok(None)
}

impl Snapshot {
    pub fn load(
        root: &str,
        file: &str,
        cargo_home: &str,
        cfg: Vec<String>,
        target: String,
        stdlib: &str,
    ) -> Result<Self, String> {
        let file = PathBuf::from(file);
        let mut manifest = None;
        let mut workspace = None;
        for directory in file.ancestors().skip(1) {
            let path = directory.join("Cargo.toml");
            if path.is_file() {
                let value = read(&path)?;
                if manifest.is_none() && value.get("package").is_some() {
                    manifest = Some(path.clone());
                }
                if value.get("workspace").is_some() {
                    workspace = Some((path, value));
                    break;
                }
            }
        }
        let lock_path = workspace
            .as_ref()
            .map(|(p, _)| p.parent().unwrap().join("Cargo.lock"))
            .or_else(|| {
                manifest
                    .as_ref()
                    .map(|p| p.parent().unwrap().join("Cargo.lock"))
            });
        let lock = if let Some(path) = lock_path.filter(|p| p.is_file()) {
            read(&path)?
                .get("package")
                .and_then(Value::as_array)
                .cloned()
                .unwrap_or_default()
        } else {
            Vec::new()
        };
        let mut requested = BTreeMap::new();
        for _ in 0..32 {
            let mut loader = Loader {
                snapshot: Self {
                    files: Vec::new(),
                    crates: Vec::new(),
                    diagnostics: Vec::new(),
                    allow_binding: true,
                },
                ids: BTreeMap::new(),
                loaded_files: HashSet::new(),
                cargo_home: cargo_home.into(),
                lock: lock.clone(),
                cfg: cfg.clone(),
                target: target.clone(),
                bytes: 0,
                requested: requested.clone(),
            };
            if let Some(ref manifest) = manifest {
                let library = loader.load_crate(&manifest, None)?;
                loader.load_binary_targets(manifest, library)?;
            } else {
                let directory = Path::new(root);
                loader.load_files(directory)?;
                loader.snapshot.crates.push(Crate {
                    root: path_string(&file),
                    name: "standalone".into(),
                    edition: "2024".into(),
                    deps: vec![],
                    cfg: loader.cfg.clone(),
                    sysroot: false,
                });
                loader.snapshot.diagnostics.push("No Cargo manifest: standalone-file crate context (edition 2024); no dependency graph is assumed.".into());
            }
            if loader.requested == requested {
                let mut configurations = Vec::new();
                for name in ["config.toml", "config"] {
                    configurations.push(loader.cargo_home.join(name));
                }
                for directory in file.ancestors().skip(1) {
                    for name in [".cargo/config.toml", ".cargo/config"] {
                        configurations.push(directory.join(name));
                    }
                    let cargo = directory.join("Cargo.toml");
                    if cargo.is_file() {
                        let parsed = read(&cargo)?;
                        if parsed.get("patch").is_some()
                            || parsed.get("replace").is_some()
                            || parsed.get("profile").and_then(Value::as_table).is_some_and(
                                |profiles| {
                                    profiles.values().any(|p| {
                                        p.get("debug-assertions").is_some()
                                            || p.get("panic").is_some()
                                    })
                                },
                            )
                        {
                            loader.snapshot.allow_binding = false;
                            loader.snapshot.diagnostics.push(format!("{}: Cargo source/profile overrides are not yet supported; semantic bindings left unknown.",cargo.display()));
                        }
                    }
                }
                for config_path in configurations {
                    if !config_path.is_file() {
                        continue;
                    }
                    let config = read(&config_path)?;
                    if config.get("patch").is_some()
                        || config.get("unstable").is_some()
                        || config.get("build").is_some_and(|b| {
                            b.get("target").is_some() || b.get("rustflags").is_some()
                        })
                        || config
                            .get("target")
                            .and_then(Value::as_table)
                            .is_some_and(|t| t.values().any(|v| v.get("rustflags").is_some()))
                    {
                        loader.snapshot.allow_binding = false;
                        loader.snapshot.diagnostics.push(format!("{}: custom Cargo target/rustflags context is not yet supported; semantic bindings left unknown rather than assuming host defaults.",config_path.display()));
                    }
                }
                if target.is_empty()
                    && loader.snapshot.files.iter().any(|f| {
                        let parsed = ast::SourceFile::parse(&f.text, Edition::CURRENT);
                        parsed
                            .tree()
                            .syntax()
                            .descendants()
                            .filter_map(ast::Attr::cast)
                            .any(|a| {
                                a.simple_name()
                                    .is_some_and(|n| n == "cfg" || n == "cfg_attr")
                            })
                    })
                {
                    loader.snapshot.allow_binding = false;
                    loader.snapshot.diagnostics.push("Target cfg unavailable: conditional-compilation semantic bindings are left unknown.".into());
                }
                if !stdlib.is_empty() {
                    loader.load_standard_library(Path::new(stdlib))?;
                }
                loader.snapshot.diagnostics.push("Procedural macros and Cargo build scripts are disabled; generated items and build-script cfg/environment are unavailable.".into());
                return Ok(loader.snapshot);
            }
            requested = loader.requested;
        }
        Err("Rust dependency feature unification exceeded 32 passes".into())
    }

    pub fn identity(&self) -> String {
        let mut hash = std::collections::hash_map::DefaultHasher::new();
        for file in &self.files {
            file.path.hash(&mut hash);
            file.text.hash(&mut hash);
            file.library.hash(&mut hash);
        }
        for krate in &self.crates {
            krate.root.hash(&mut hash);
            krate.name.hash(&mut hash);
            krate.edition.hash(&mut hash);
            krate.sysroot.hash(&mut hash);
            krate.cfg.hash(&mut hash);
            for dep in &krate.deps {
                dep.name.hash(&mut hash);
                dep.crate_index.hash(&mut hash);
            }
        }
        format!("{:x}", hash.finish())
    }
}

impl Loader {
    fn load_standard_library(&mut self, directory: &Path) -> Result<(), String> {
        let project_crates = self.snapshot.crates.len();
        let project_files = self.snapshot.files.len();
        self.load_files(directory)?;
        for file in &mut self.snapshot.files[project_files..] {
            file.library = true;
        }
        let mut ids = BTreeMap::new();
        for name in [
            "core",
            "alloc",
            "std",
            "proc_macro",
            "panic_abort",
            "panic_unwind",
            "unwind",
            "std_detect",
            "compiler-builtins/compiler-builtins",
            "test",
        ] {
            let root = directory.join(name).join("src/lib.rs");
            if !root.is_file() {
                continue;
            }
            let short = name.rsplit('/').next().unwrap().replace('-', "_");
            let manifest = read(&directory.join(name).join("Cargo.toml"))?;
            let edition = manifest
                .get("package")
                .and_then(|p| string(p, "edition"))
                .unwrap_or("2024")
                .to_owned();
            let mut cfg = self.cfg.clone();
            cfg.push("rust_analyzer".into());
            cfg.push("feature=\"rustc-dep-of-std\"".into());
            ids.insert(short.clone(), self.snapshot.crates.len());
            self.snapshot.crates.push(Crate {
                root: path_string(&fs::canonicalize(root).map_err(|e| e.to_string())?),
                name: short,
                edition,
                deps: vec![],
                cfg,
                sysroot: true,
            });
        }
        for (name, dependencies) in [
            ("alloc", &["core", "compiler_builtins"][..]),
            (
                "std",
                &[
                    "alloc",
                    "core",
                    "panic_unwind",
                    "panic_abort",
                    "unwind",
                    "std_detect",
                ][..],
            ),
            ("proc_macro", &["std", "core"][..]),
            ("panic_abort", &["core"][..]),
            ("panic_unwind", &["alloc", "core", "unwind"][..]),
            ("unwind", &["core"][..]),
            ("std_detect", &["alloc", "core"][..]),
            ("test", &["std", "core"][..]),
        ] {
            if let Some(&id) = ids.get(name) {
                for dependency in dependencies {
                    if let Some(&target) = ids.get(*dependency) {
                        self.snapshot.crates[id].deps.push(Dependency {
                            name: (*dependency).into(),
                            crate_index: target,
                        });
                    }
                }
            }
        }
        for krate in &mut self.snapshot.crates[..project_crates] {
            for name in ["core", "alloc", "std", "proc_macro"] {
                if let Some(&target) = ids.get(name) {
                    if !krate.deps.iter().any(|d| d.name == name) {
                        krate.deps.push(Dependency {
                            name: name.into(),
                            crate_index: target,
                        });
                    }
                }
            }
        }
        self.snapshot.diagnostics.push("Standard-library sources loaded without running their build scripts; external sysroot implementation dependencies/generated platform code may be unresolved.".into());
        Ok(())
    }

    fn load_binary_targets(&mut self, manifest_path: &Path, library: usize) -> Result<(), String> {
        let manifest = read(manifest_path)?;
        let directory = manifest_path.parent().unwrap();
        let mut binaries = BTreeMap::new();
        let autobins = manifest
            .get("package")
            .and_then(|p| p.get("autobins"))
            .and_then(Value::as_bool)
            != Some(false);
        if autobins && directory.join("src/main.rs").is_file() {
            binaries.insert(
                directory.join("src/main.rs"),
                self.snapshot.crates[library].name.clone(),
            );
        }
        if let Some(entries) = manifest.get("bin").and_then(Value::as_array) {
            for entry in entries {
                if let Some(path) = string(entry, "path") {
                    binaries.insert(
                        directory.join(path),
                        string(entry, "name").unwrap_or("binary").replace('-', "_"),
                    );
                }
            }
        }
        let bin_directory = directory.join("src/bin");
        if autobins && bin_directory.is_dir() {
            for entry in fs::read_dir(bin_directory).map_err(|e| e.to_string())? {
                let entry = entry.map_err(|e| e.to_string())?;
                let path = entry.path();
                if path.extension().is_some_and(|e| e == "rs") {
                    binaries.insert(
                        path.clone(),
                        path.file_stem()
                            .unwrap()
                            .to_string_lossy()
                            .replace('-', "_"),
                    );
                } else if entry.file_type().map_err(|e| e.to_string())?.is_dir()
                    && path.join("main.rs").is_file()
                {
                    binaries.insert(
                        path.join("main.rs"),
                        entry.file_name().to_string_lossy().replace('-', "_"),
                    );
                }
            }
        }
        for (root, name) in binaries {
            let root = path_string(&fs::canonicalize(root).map_err(|e| e.to_string())?);
            let original = &self.snapshot.crates[library];
            if root == original.root {
                continue;
            }
            let mut deps = original
                .deps
                .iter()
                .map(|d| Dependency {
                    name: d.name.clone(),
                    crate_index: d.crate_index,
                })
                .collect::<Vec<_>>();
            deps.push(Dependency {
                name: original.name.clone(),
                crate_index: library,
            });
            self.snapshot.crates.push(Crate {
                root,
                name,
                edition: original.edition.clone(),
                deps,
                cfg: original.cfg.clone(),
                sysroot: false,
            });
        }
        Ok(())
    }

    fn load_crate(
        &mut self,
        manifest_path: &Path,
        requested: Option<Vec<String>>,
    ) -> Result<usize, String> {
        let manifest_path = fs::canonicalize(manifest_path)
            .map_err(|e| format!("{}: {e}", manifest_path.display()))?;
        let is_dependency = requested.is_some();
        self.requested
            .entry(manifest_path.clone())
            .or_default()
            .extend(requested.unwrap_or_else(|| vec!["default".into()]));
        if let Some(id) = self.ids.get(&manifest_path) {
            return Ok(*id);
        }
        if self.ids.len() >= 512 {
            return Err("Rust dependency graph exceeds 512 crates".into());
        }
        let manifest = read(&manifest_path)?;
        let package = manifest
            .get("package")
            .ok_or("dependency manifest has no package")?;
        let directory = manifest_path.parent().unwrap();
        let workspace = workspace_for(directory, &manifest)?;
        let name = string(package, "name")
            .ok_or("package has no name")?
            .to_owned();
        let library = manifest.get("lib");
        if is_dependency && library.is_none() && !directory.join("src/lib.rs").is_file() {
            return Err(format!("{name}: dependency has no library target"));
        }
        let root = library
            .and_then(|l| string(l, "path"))
            .map(|p| directory.join(p))
            .unwrap_or_else(|| {
                if directory.join("src/lib.rs").is_file() {
                    directory.join("src/lib.rs")
                } else {
                    directory.join("src/main.rs")
                }
            });
        if !root.is_file() {
            return Err(format!("{}: crate root unavailable", root.display()));
        }
        let edition = string(package, "edition")
            .map(str::to_owned)
            .or_else(|| {
                workspace.as_ref().and_then(|(_, w)| {
                    w.get("workspace")?
                        .get("package")?
                        .get("edition")?
                        .as_str()
                        .map(str::to_owned)
                })
            })
            .unwrap_or_else(|| "2015".into());
        let mut features = self.requested[&manifest_path].clone();
        if let Some(table) = manifest.get("features").and_then(Value::as_table) {
            loop {
                let mut extra = Vec::new();
                for feature in &features {
                    if let Some(values) = table.get(feature).and_then(Value::as_array) {
                        extra.extend(values.iter().filter_map(Value::as_str).map(str::to_owned));
                    }
                }
                let count = features.len();
                features.extend(extra);
                if count == features.len() {
                    break;
                }
            }
        }
        let mut cfg = self.cfg.clone();
        cfg.extend(
            features
                .iter()
                .filter(|f| !f.contains('/') && !f.starts_with("dep:"))
                .map(|f| format!("feature=\"{f}\"")),
        );
        cfg.sort();
        let id = self.snapshot.crates.len();
        self.ids.insert(manifest_path.clone(), id);
        let display = library
            .and_then(|l| string(l, "name"))
            .unwrap_or(&name)
            .replace('-', "_");
        self.snapshot.crates.push(Crate {
            root: path_string(&root),
            name: display,
            edition,
            deps: Vec::new(),
            cfg,
            sysroot: false,
        });
        self.load_files(directory)?;
        if directory.join("build.rs").is_file() || package.get("build").is_some() {
            self.snapshot.diagnostics.push(format!(
                "{name}: build script is disabled; generated code/cfg/environment is partial."
            ));
        }
        if library
            .and_then(|l| l.get("proc-macro"))
            .and_then(Value::as_bool)
            == Some(true)
        {
            self.snapshot
                .diagnostics
                .push(format!("{name}: procedural-macro execution is disabled."));
        }
        let mut dependencies = BTreeMap::new();
        if let Some(table) = manifest.get("dependencies").and_then(Value::as_table) {
            dependencies.extend(table.iter().map(|(k, v)| (k.clone(), v.clone())));
        }
        if let Some(targets) = manifest.get("target").and_then(Value::as_table) {
            let cfg = self
                .cfg
                .iter()
                .filter_map(|s| s.parse().ok())
                .collect::<Vec<_>>();
            for (condition, value) in targets {
                let platform: Platform = condition
                    .parse()
                    .map_err(|e| format!("invalid target condition {condition}: {e}"))?;
                if platform.matches(&self.target, &cfg) {
                    if let Some(table) = value.get("dependencies").and_then(Value::as_table) {
                        dependencies.extend(table.iter().map(|(k, v)| (k.clone(), v.clone())));
                    }
                }
            }
        }
        for (alias, mut value) in dependencies {
            let mut relative = directory.to_path_buf();
            if value.get("workspace").and_then(Value::as_bool) == Some(true) {
                let inherited = workspace
                    .as_ref()
                    .and_then(|(_, w)| w.get("workspace")?.get("dependencies")?.get(&alias));
                if let Some(inherited) = inherited {
                    let mut merged = inherited.as_table().cloned().unwrap_or_else(|| {
                        let mut table = toml::map::Map::new();
                        table.insert("version".into(), inherited.clone());
                        table
                    });
                    let mut inherited_features = merged
                        .get("features")
                        .and_then(Value::as_array)
                        .cloned()
                        .unwrap_or_default();
                    if let Some(table) = value.as_table() {
                        if let Some(features) = table.get("features").and_then(Value::as_array) {
                            inherited_features.extend(features.clone());
                        }
                        merged.extend(table.clone());
                    }
                    if !inherited_features.is_empty() {
                        merged.insert("features".into(), Value::Array(inherited_features));
                    }
                    value = Value::Table(merged);
                    relative = workspace
                        .as_ref()
                        .unwrap()
                        .0
                        .parent()
                        .unwrap()
                        .to_path_buf();
                } else {
                    self.snapshot
                        .diagnostics
                        .push(format!("{name}: workspace dependency {alias} unavailable."));
                    continue;
                }
            }
            if value.get("optional").and_then(Value::as_bool) == Some(true)
                && !features.contains(&alias)
                && !features.contains(&format!("dep:{alias}"))
                && !features
                    .iter()
                    .any(|f| f.starts_with(&(alias.clone() + "/")))
            {
                continue;
            }
            let package_name = string(&value, "package").unwrap_or(&alias);
            let dep_path = if let Some(path) = string(&value, "path") {
                Some(relative.join(path).join("Cargo.toml"))
            } else {
                self.cached_dependency(
                    package_name,
                    value.as_str().or_else(|| string(&value, "version")),
                    string(&value, "git"),
                    &name,
                    string(package, "version"),
                )?
            };
            if let Some(path) = dep_path {
                let mut requested = value
                    .get("features")
                    .and_then(Value::as_array)
                    .map(|a| {
                        a.iter()
                            .filter_map(Value::as_str)
                            .map(str::to_owned)
                            .collect::<Vec<_>>()
                    })
                    .unwrap_or_default();
                if value.get("default-features").and_then(Value::as_bool) != Some(false) {
                    requested.push("default".into());
                }
                requested.extend(
                    features
                        .iter()
                        .filter_map(|f| f.strip_prefix(&(alias.clone() + "/")).map(str::to_owned)),
                );
                requested.extend(
                    features
                        .iter()
                        .filter_map(|f| f.strip_prefix(&(alias.clone() + "?/")).map(str::to_owned)),
                );
                match self.load_crate(&path, Some(requested)) {
                    Ok(target) => self.snapshot.crates[id].deps.push(Dependency {
                        name: alias.replace('-', "_"),
                        crate_index: target,
                    }),
                    Err(error) => self
                        .snapshot
                        .diagnostics
                        .push(format!("{name}: dependency {alias}: {error}")),
                }
            } else {
                self.snapshot.diagnostics.push(format!(
                    "{name}: dependency {alias} unavailable on disk; no download attempted."
                ));
            }
        }
        Ok(id)
    }

    fn cached_dependency(
        &self,
        name: &str,
        requirement: Option<&str>,
        git: Option<&str>,
        parent_name: &str,
        parent_version: Option<&str>,
    ) -> Result<Option<PathBuf>, String> {
        let requirement = requirement
            .map(VersionReq::parse)
            .transpose()
            .map_err(|e| e.to_string())?;
        let parent_edges = self
            .lock
            .iter()
            .filter(|p| {
                string(p, "name") == Some(parent_name)
                    && parent_version.is_none_or(|v| string(p, "version") == Some(v))
            })
            .filter_map(|p| p.get("dependencies").and_then(Value::as_array))
            .flatten()
            .filter_map(Value::as_str)
            .filter(|edge| edge.split_whitespace().next() == Some(name))
            .collect::<Vec<_>>();
        let locked = self
            .lock
            .iter()
            .filter(|p| string(p, "name") == Some(name))
            .filter(|p| {
                string(p, "source").is_some_and(|s| match git {
                    Some(url) => s
                        .strip_prefix("git+")
                        .is_some_and(|s| s.split(['?', '#']).next() == Some(url)),
                    None => s.starts_with("registry+"),
                })
            })
            .filter(|p| {
                string(p, "version").is_some_and(|v| {
                    requirement
                        .as_ref()
                        .is_none_or(|r| Version::parse(v).is_ok_and(|v| r.matches(&v)))
                })
            })
            .filter(|p| {
                parent_edges.is_empty()
                    || parent_edges.iter().any(|edge| {
                        let mut parts = edge.split_whitespace();
                        parts.next();
                        let version_matches =
                            parts.next().is_none_or(|v| string(p, "version") == Some(v));
                        let source = edge
                            .split_once('(')
                            .map(|(_, source)| source.trim_end_matches(')'));
                        version_matches && source.is_none_or(|s| string(p, "source") == Some(s))
                    })
            })
            .collect::<Vec<_>>();
        if locked.len() > 1 {
            return Err(format!(
                "multiple locked versions/source identities of {name}; exact dependency edge is ambiguous"
            ));
        }
        let Some(locked) = locked.first() else {
            return Ok(None);
        };
        let version = string(locked, "version").unwrap();
        let mut matches = Vec::new();
        if git.is_some() {
            let revision = string(locked, "source")
                .and_then(|s| s.rsplit_once('#'))
                .map(|(_, rev)| rev)
                .ok_or("locked Git source has no revision")?;
            let checkouts = self.cargo_home.join("git/checkouts");
            if checkouts.is_dir() {
                for repository in fs::read_dir(checkouts).map_err(|e| e.to_string())? {
                    for checkout in fs::read_dir(repository.map_err(|e| e.to_string())?.path())
                        .map_err(|e| e.to_string())?
                    {
                        let checkout = checkout.map_err(|e| e.to_string())?.path();
                        let head = checkout.join(".git/HEAD");
                        if fs::read_to_string(head).is_ok_and(|s| s.trim() == revision) {
                            Self::git_manifests(&checkout, name, version, &mut matches)?;
                        }
                    }
                }
            }
        } else {
            let registry = self.cargo_home.join("registry/src");
            if registry.is_dir() {
                for entry in fs::read_dir(registry).map_err(|e| e.to_string())? {
                    let path = entry
                        .map_err(|e| e.to_string())?
                        .path()
                        .join(format!("{name}-{version}/Cargo.toml"));
                    if path.is_file() {
                        let checksum = path.parent().unwrap().join(".cargo-checksum.json");
                        if let Some(expected) = string(locked, "checksum") {
                            let checksum: serde_json::Value = serde_json::from_str(
                                &fs::read_to_string(&checksum).map_err(|e| e.to_string())?,
                            )
                            .map_err(|e| e.to_string())?;
                            if checksum.get("package").and_then(serde_json::Value::as_str)
                                != Some(expected)
                            {
                                continue;
                            }
                        }
                        matches.push(path);
                    }
                }
            }
        }
        if matches.len() > 1 {
            return Err(format!(
                "multiple cached source identities for {name}-{version}"
            ));
        }
        Ok(matches.pop())
    }

    fn git_manifests(
        directory: &Path,
        name: &str,
        version: &str,
        matches: &mut Vec<PathBuf>,
    ) -> Result<(), String> {
        Self::git_manifests_at(directory, name, version, matches, 0)
    }

    fn git_manifests_at(
        directory: &Path,
        name: &str,
        version: &str,
        matches: &mut Vec<PathBuf>,
        depth: usize,
    ) -> Result<(), String> {
        if depth > 128 {
            return Err("Rust Git source discovery exceeds 128 directory levels".into());
        }
        let manifest = directory.join("Cargo.toml");
        if manifest.is_file() {
            let value = read(&manifest)?;
            if value.get("package").is_some_and(|p| {
                string(p, "name") == Some(name) && string(p, "version") == Some(version)
            }) {
                matches.push(manifest);
            }
        }
        for entry in fs::read_dir(directory).map_err(|e| e.to_string())? {
            let entry = entry.map_err(|e| e.to_string())?;
            if entry.file_type().map_err(|e| e.to_string())?.is_dir()
                && !matches!(entry.file_name().to_str(), Some(".git" | "target"))
            {
                Self::git_manifests_at(&entry.path(), name, version, matches, depth + 1)?;
            }
        }
        Ok(())
    }

    fn load_files(&mut self, directory: &Path) -> Result<(), String> {
        self.load_files_at(directory, 0)
    }
    fn load_files_at(&mut self, directory: &Path, depth: usize) -> Result<(), String> {
        if depth > 128 {
            return Err("Rust source discovery exceeds 128 directory levels".into());
        }
        let mut entries = fs::read_dir(directory)
            .map_err(|e| format!("{}: {e}", directory.display()))?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|e| e.to_string())?;
        entries.sort_by_key(|e| e.path());
        for entry in entries {
            let kind = entry.file_type().map_err(|e| e.to_string())?;
            if kind.is_symlink() {
                continue;
            }
            if kind.is_dir() {
                if !matches!(
                    entry.file_name().to_str(),
                    Some("target" | ".git" | ".cop" | "node_modules")
                ) {
                    self.load_files_at(&entry.path(), depth + 1)?;
                }
            } else if entry.path().extension().is_some_and(|e| e == "rs") {
                let path = fs::canonicalize(entry.path()).map_err(|e| e.to_string())?;
                if !self.loaded_files.insert(path.clone()) {
                    continue;
                }
                let text =
                    fs::read_to_string(&path).map_err(|e| format!("{}: {e}", path.display()))?;
                self.bytes += text.len();
                if self.bytes > 128 * 1024 * 1024 || self.snapshot.files.len() >= 20000 {
                    return Err("Rust semantic source snapshot exceeds 128 MiB/20000 files".into());
                }
                self.snapshot.files.push(File {
                    path: path_string(&path),
                    text,
                    library: false,
                });
            }
        }
        Ok(())
    }
}
