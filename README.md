# Octokit.ReleaseNotes

A dotnet core 2.0 commandline app that generates release notes for [octokit/octokit.net](https://github.com/octokit/octokit.net) in our [preferred format](https://github.com/octokit/octokit.net/releases)

```
Usage: Octokit.ReleaseNotes generate [arguments] [options]

Arguments:
  from  GitHub reference (commit, branch, tag) of the last release
  to    GitHub reference (commit, branch, tag) of the current release

Options:
  -? | -h | --help           Show help information
  --repo|-r <owner/name>     The GitHub repository (default octokit/octokit.net)
  --concurrency|-c <number>  Number of Pull Requests to load concurrently (default 10)
  --out|-o <file>            Output release notes to file
```