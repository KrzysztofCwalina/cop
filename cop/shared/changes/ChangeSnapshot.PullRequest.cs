using System.Text.Json;

namespace Cop.Core;

public sealed partial class ChangeSnapshot
{
    private sealed record PullRequestMetadata(
        string Base, string Head, string BaseRef, string HeadRef,
        string BaseRepository, string HeadRepository, string Number, string Url);

    private PullRequestMetadata LoadPullRequest()
    {
        var result = command.Run(RepositoryRoot, "gh", "pr", "view", options.Pr!, "--json",
            "baseRefOid,headRefOid,baseRefName,headRefName,headRepository,headRepositoryOwner,url");
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Cannot read exact PR metadata: {result.Error.Trim()}. Install/authenticate gh and verify --pr; no branch/full fallback was selected.");
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            var root = json.RootElement;
            var url = RequiredString(root, "url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0)
                throw new InvalidOperationException("PR metadata contains an invalid repository URL.");
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length != 4 || parts[2] != "pull" || !parts[3].All(char.IsAsciiDigit)
                || !long.TryParse(parts[3], out var number) || number <= 0)
                throw new InvalidOperationException("PR metadata contains an invalid pull request identity.");
            var baseRepository = $"{uri.Host}/{parts[0]}/{parts[1]}".ToLowerInvariant();
            var headRepository = root.GetProperty("headRepository");
            var owner = RequiredString(root.GetProperty("headRepositoryOwner"), "login");
            var headName = RequiredString(headRepository, "name");
            if (!IsIdentityPart(owner) || !IsIdentityPart(headName) || !IsIdentityPart(parts[0]) || !IsIdentityPart(parts[1]))
                throw new InvalidOperationException("PR metadata contains an invalid repository identity.");
            var baseSha = RequiredString(root, "baseRefOid");
            var headSha = RequiredString(root, "headRefOid");
            if (!IsObjectId(baseSha) || !IsObjectId(headSha))
                throw new InvalidOperationException("PR metadata must include exact base/head commit IDs.");
            var metadata = new PullRequestMetadata(baseSha.ToLowerInvariant(), headSha.ToLowerInvariant(),
                RequiredString(root, "baseRefName"), RequiredString(root, "headRefName"),
                baseRepository, $"{uri.Host}/{owner}/{headName}".ToLowerInvariant(), parts[3], url);
            if (long.TryParse(options.Pr, out var requestedNumber) && requestedNumber != number)
                throw new InvalidOperationException("PR metadata does not match the requested number.");
            if (Uri.TryCreate(options.Pr, UriKind.Absolute, out var requestedUri)
                && !string.Equals(requestedUri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                    uri.GetLeftPart(UriPartial.Path).TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("PR metadata does not match the requested repository and number.");
            return metadata;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Cannot validate PR metadata: {exception.Message}", exception);
        }
    }

    private string FindPullRequestRemote(PullRequestMetadata metadata)
    {
        foreach (var remote in Git("remote").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var url = Git("remote", "get-url", "--", remote).Trim();
            if (RepositoryIdentity(url) == metadata.BaseRepository)
                return remote;
        }
        throw new InvalidOperationException(
            $"The PR targets {metadata.BaseRepository}, which is not a remote of this worktree. Use the correct repository; no checkout was performed.");
    }

    private void EnsurePullRequestCommit(string sha, string remote, string fetchRef)
    {
        if (RunGit("cat-file", "-e", sha + "^{commit}").ExitCode == 0)
            return;
        var fetched = RunGit("fetch", "--no-tags", "--no-recurse-submodules", "--", remote, fetchRef);
        if (fetched.ExitCode != 0 || RunGit("cat-file", "-e", sha + "^{commit}").ExitCode != 0)
            throw new InvalidOperationException(
                $"The exact PR commit {sha} is unavailable. Fetch complete PR history from '{remote}' and retry. {fetched.Error.Trim()}");
    }

    internal static string? RepositoryIdentity(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "ssh" or "git")
            return Identity(uri.Host, uri.AbsolutePath);
        var colon = url.IndexOf(':');
        if (colon <= 0 || colon == url.Length - 1 || url.IndexOf(':', colon + 1) >= 0)
            return null;
        var authority = url[..colon];
        var at = authority.IndexOf('@');
        if (at >= 0)
        {
            if (at == 0 || at == authority.Length - 1 || authority.IndexOf('@', at + 1) >= 0
                || authority.AsSpan(0, at).Contains('/'))
                return null;
            authority = authority[(at + 1)..];
        }
        return IsIdentityPart(authority) ? Identity(authority, url[(colon + 1)..]) : null;

        static string? Identity(string host, string path)
        {
            path = path.Trim('/');
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                path = path[..^4];
            var parts = path.Split('/');
            return parts.Length == 2 && parts.All(IsIdentityPart) ? $"{host}/{path}".ToLowerInvariant() : null;
        }
    }

    private static bool IsIdentityPart(string value) =>
        value.Length != 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
        && value is not ("." or "..");

    private static bool IsObjectId(string value) =>
        value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);

    private static string RequiredString(JsonElement element, string property) =>
        element.GetProperty(property).GetString() is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"PR metadata is missing '{property}'.");
}
