using System;
using System.IO;
using Octokit;

namespace OctokitReleaseNotes
{
    public class Program
    {
        static void Main(string[] args)
        {
            Credentials githubCreds = null;

            var githubToken = Environment.GetEnvironmentVariable("OCTOKIT_OAUTHTOKEN");
            if (githubToken != null)
            {
                githubCreds = new Credentials(githubToken);
            }
            else
            {
                var githubUsername = Environment.GetEnvironmentVariable("OCTOKIT_GITHUBUSERNAME");
                var githubPassword = Environment.GetEnvironmentVariable("OCTOKIT_GITHUBPASSWORD");
                githubCreds = new Credentials(githubUsername, githubPassword);
            }

            var githubClient = new GitHubClient(new ProductHeaderValue("OctokitReleaseNotes"))
            {
                Credentials = githubCreds
            };

            var generator = new ReleaseNotes(githubClient);
            var releaseNotes = generator.GetReleaseNotes("octokit", "octokit.net", "v0.24.0", "master").Result;

            if (false)
            {
                using (var writer = File.CreateText("c:\\temp\\releasenotes.txt"))
                {
                    writer.WriteLine(releaseNotes);
                }
            }

            Console.WriteLine(releaseNotes);
            Console.WriteLine("Press a key to continue...");
            Console.Read();
        }
    }
}