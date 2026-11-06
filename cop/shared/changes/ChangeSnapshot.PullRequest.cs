// Resolves pull-request metadata and diffs into source-change snapshots.

using System.Text.Json;

namespace Cop.Core;

/// <summary>Resolves exact pull-request revisions and repository metadata for review.</summary>
public sealed partial class ChangeSnapshot
{
    private sealed record PullRequestMetadata(
        string Base, string Head, string BaseRef, string HeadRef,
        string BaseRepository, string HeadRepository, string Number, string Url);

    private async ValueTask<PullRequestMetadata> LoadPullRequestAsync(CancellationToken cancellationToken = default)
    {
        ChangeCommandResult result = (await _command.RunAsync(RepositoryRoot, "gh",
            ["pr", "view", _options.Pr!, "--json",
            "baseRefOid,headRefOid,baseRefName,headRefName,headRepository,headRepositoryOwner,url"], cancellationToken: cancellationToken));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Cannot read exact PR metadata: {result.Error.Trim()}. Install/authenticate gh and verify --pr; no branch/full fallback was selected.");
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(result.Output);
            JsonElement root = json.RootElement;
            string url = RequiredString(root, "url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0)
            {
                throw new InvalidOperationException("PR metadata contains an invalid repository URL.");
            }

            string[] parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length != 4 || parts[2] != "pull" || !parts[3].All(char.IsAsciiDigit)
                || !long.TryParse(parts[3], out long number) || number <= 0)
            {
                throw new InvalidOperationException("PR metadata contains an invalid pull request identity.");
            }

            string baseRepository = $"{uri.Host}/{parts[0]}/{parts[1]}".ToLowerInvariant();
            JsonElement headRepository = root.GetProperty("headRepository");
            string owner = RequiredString(root.GetProperty("headRepositoryOwner"), "login");
            string headName = RequiredString(headRepository, "name");
            if (!IsIdentityPart(owner) || !IsIdentityPart(headName) || !IsIdentityPart(parts[0]) || !IsIdentityPart(parts[1]))
            {
                throw new InvalidOperationException("PR metadata contains an invalid repository identity.");
            }

            string baseSha = RequiredString(root, "baseRefOid");
            string headSha = RequiredString(root, "headRefOid");
            if (!IsObjectId(baseSha) || !IsObjectId(headSha))
            {
                throw new InvalidOperationException("PR metadata must include exact base/head commit IDs.");
            }

            PullRequestMetadata metadata = new PullRequestMetadata(baseSha.ToLowerInvariant(), headSha.ToLowerInvariant(),
                RequiredString(root, "baseRefName"), RequiredString(root, "headRefName"),
                baseRepository, $"{uri.Host}/{owner}/{headName}".ToLowerInvariant(), parts[3], url);
            if (long.TryParse(_options.Pr, out long requestedNumber) && requestedNumber != number)
            {
                throw new InvalidOperationException("PR metadata does not match the requested number.");
            }

            if (Uri.TryCreate(_options.Pr, UriKind.Absolute, out Uri? requestedUri)
                && !string.Equals(requestedUri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                    uri.GetLeftPart(UriPartial.Path).TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("PR metadata does not match the requested repository and number.");
            }

            return metadata;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Cannot validate PR metadata: {exception.Message}", exception);
        }
    }

    private async ValueTask<string> FindPullRequestRemoteAsync(PullRequestMetadata metadata, CancellationToken cancellationToken = default)
    {
        foreach (string remote in (await GitAsync(cancellationToken, "remote")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string url = (await GitAsync(cancellationToken, "remote", "get-url", "--", remote)).Trim();
            if (RepositoryIdentity(url) == metadata.BaseRepository)
            {
                return remote;
            }
        }
        throw new InvalidOperationException(
            $"The PR targets {metadata.BaseRepository}, which is not a remote of this worktree. Use the correct repository; no checkout was performed.");
    }

    private async ValueTask EnsurePullRequestCommitAsync(string sha, string remote, string fetchRef, CancellationToken cancellationToken = default)
    {
        if ((await RunGitAsync(cancellationToken, "cat-file", "-e", sha + "^{commit}")).ExitCode == 0)
        {
            return;
        }

        ChangeCommandResult fetched = (await RunGitAsync(cancellationToken, "fetch", "--no-tags", "--no-recurse-submodules", "--", remote, fetchRef));
        if (fetched.ExitCode != 0 || (await RunGitAsync(cancellationToken, "cat-file", "-e", sha + "^{commit}")).ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The exact PR commit {sha} is unavailable. Fetch complete PR history from '{remote}' and retry. {fetched.Error.Trim()}");
        }
    }

    internal static string? RepositoryIdentity(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "https" or "http" or "ssh" or "git")
        {
            return Identity(uri.Host, uri.AbsolutePath);
        }

        int colon = url.IndexOf(':');
        if (colon <= 0 || colon == url.Length - 1 || url.IndexOf(':', colon + 1) >= 0)
        {
            return null;
        }

        string authority = url[..colon];
        int at = authority.IndexOf('@');
        if (at >= 0)
        {
            if (at == 0 || at == authority.Length - 1 || authority.IndexOf('@', at + 1) >= 0
                || authority.AsSpan(0, at).Contains('/'))
            {
                return null;
            }

            authority = authority[(at + 1)..];
        }
        return IsIdentityPart(authority) ? Identity(authority, url[(colon + 1)..]) : null;

        static string? Identity(string host, string path)
        {
            path = path.Trim('/');
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^4];
            }

            string[] parts = path.Split('/');
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
