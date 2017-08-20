using System;
using System.IO;
using Octokit;
using Microsoft.Extensions.CommandLineUtils;

namespace OctokitReleaseNotes
{
    public class Program
    {
        static int Main(string[] args)
        {
            var app = new CommandLineApplication();
            app.Name = "octorelease";
            app.HelpOption("-? | -h | --help");

            // Display help when no commands specified
            app.OnExecute(() =>
            {
                app.ShowHelp();
                return 1;
            });

            // Add the generate command
            app.Command("generate", command =>
            {
                command.Description = "Generate release notes";
                command.HelpOption("-? | -h | --help");

                // From and To references
                var fromRef = command.Argument("from", "FROM reference", false);
                var toRef = command.Argument("to", "TO reference", false);

                // Number of PR's to load at once
                var concurrency = command.Option("--concurrency <number>", "Number of Pull Requests to load concurrently (default 10)", CommandOptionType.SingleValue);

                // Output to file
                var releaseNotesFile = command.Option("--out <file>", "Output release notes to file", CommandOptionType.SingleValue);

                command.OnExecute(() =>
                {
                    var batchSize = 10;
                    if (concurrency.HasValue())
                    {
                        try
                        {
                            batchSize = Convert.ToInt32(concurrency.Value());
                        }
                        catch (Exception ex)
                        {
                            throw new ArgumentException("Invalid concurrency value specified", ex);
                        }
                    }

                    var consoleColour = Console.ForegroundColor;
                    try
                    {
                        GenerateReleaseNotes(fromRef.Value, toRef.Value, batchSize, releaseNotesFile.Value());
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        return HandleException(ex);
                    }
                    finally
                    {
                        Console.ForegroundColor = consoleColour;
                    }
                });
            });

            return app.Execute(args);
        }

        private static int HandleException(Exception ex)
        {
            if (ex.InnerException != null)
            {
                ex = ex.InnerException;
            }

            var extraInfo = "";
            if (ex is AbuseException)
            {
                extraInfo = $"Try again in {(ex as AbuseException).RetryAfterSeconds} seconds";
            }
            else if (ex is RateLimitExceededException)
            {
                extraInfo = $"Rate limit resets at {(ex as RateLimitExceededException).Reset.ToLocalTime()}";
            }

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message} {extraInfo}");

            return 1;
        }

        static Credentials GetGithubCredentials()
        {
            Credentials githubCreds = null;

            var githubToken = Environment.GetEnvironmentVariable("OCTOKIT_OAUTHTOKEN");
            if (githubToken != null)
            {
                Console.WriteLine($"Authenticating using OAUTH token (*******{githubToken.Substring(githubToken.Length - 6)})");
                githubCreds = new Credentials(githubToken);
            }
            else
            {
                var githubUsername = Environment.GetEnvironmentVariable("OCTOKIT_GITHUBUSERNAME");
                var githubPassword = Environment.GetEnvironmentVariable("OCTOKIT_GITHUBPASSWORD");
                Console.WriteLine($"Authenticating using BASIC auth ({githubUsername})");
                githubCreds = new Credentials(githubUsername, githubPassword);
            }

            return githubCreds;
        }

        static void GenerateReleaseNotes(string fromRef, string toRef, int batchSize, string releaseNotesFile)
        {
            Console.WriteLine("Initializing Github connection");
            var githubClient = new GitHubClient(new ProductHeaderValue("OctokitReleaseNotes"))
            {
                Credentials = GetGithubCredentials()
            };

            Console.WriteLine($"Generating release notes for PR's between {fromRef} and {toRef}");
            var generator = new ReleaseNotes(githubClient);
            var releaseNotes = generator.GetReleaseNotes("octokit", "octokit.net", fromRef, toRef, batchSize).Result;

            if (!string.IsNullOrEmpty(releaseNotesFile))
            {
                Console.WriteLine($"Writing release notes to {releaseNotesFile}");
                using (var writer = File.CreateText(releaseNotesFile))
                {
                    writer.WriteLine(releaseNotes);
                }
            }

            Console.WriteLine("\r\n\r\nRelease Notes:\r\n");
            Console.WriteLine(releaseNotes);
        }
    }
}