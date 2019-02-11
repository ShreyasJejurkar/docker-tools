// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.DotNet.ImageBuilder.ViewModel;

namespace Microsoft.DotNet.ImageBuilder.Commands
{
    public class BuildCommand : DockerRegistryCommand<BuildOptions>
    {
        private IEnumerable<TagInfo> BuiltTags { get; set; } = Enumerable.Empty<TagInfo>();

        public BuildCommand() : base()
        {
        }

        public override Task ExecuteAsync()
        {
            PullBaseImages();
            BuildImages();

            if (BuiltTags.Any())
            {
                PushImages();
            }

            WriteBuildSummary();

            return Task.CompletedTask;
        }

        private void BuildImages()
        {
            Logger.WriteHeading("BUILDING IMAGES");
            foreach (ImageInfo image in Manifest.GetFilteredImages())
            {
                foreach (PlatformInfo platform in image.FilteredPlatforms)
                {
                    bool createdPrivateDockerfile = UpdateDockerfileFromCommands(platform, out string dockerfilePath);

                    try
                    {
                        InvokeBuildHook("pre-build", platform.BuildContextPath);

                        // Tag the built images with the shared tags as well as the platform tags.
                        // Some tests and image FROM instructions depend on these tags.
                        IEnumerable<string> platformTags = platform.Tags
                            .Select(tag => tag.FullyQualifiedName)
                            .ToArray();
                        string tagArgs = GetDockerTagArgs(image, platformTags);
                        string buildArgs = GetDockerBuildArgs(platform);
                        string dockerArgs = $"build {tagArgs} -f {dockerfilePath}{buildArgs} {platform.BuildContextPath}";

                        if (Options.IsRetryEnabled)
                        {
                            ExecuteHelper.ExecuteWithRetry("docker", dockerArgs, Options.IsDryRun);
                        }
                        else
                        {
                            ExecuteHelper.Execute("docker", dockerArgs, Options.IsDryRun);
                        }

                        InvokeBuildHook("post-build", platform.BuildContextPath);
                        BuiltTags = BuiltTags.Concat(platform.Tags);
                    }
                    finally
                    {
                        if (createdPrivateDockerfile)
                        {
                            File.Delete(dockerfilePath);
                        }
                    }
                }
            }

            BuiltTags = BuiltTags.ToArray();
        }

        private string GetDockerBuildArgs(PlatformInfo platform)
        {
            IEnumerable<string> buildArgs = platform.BuildArgs
                .Select(buildArg => $" --build-arg {buildArg.Key}={buildArg.Value}");
            return string.Join(string.Empty, buildArgs);
        }

        private string GetDockerTagArgs(ImageInfo image, IEnumerable<string> platformTags)
        {
            IEnumerable<string> allTags = image.SharedTags
                .Select(tag => tag.FullyQualifiedName)
                .Concat(platformTags);
            return $"-t {string.Join(" -t ", allTags)}";
        }

        private void InvokeBuildHook(string hookName, string buildContextPath)
        {
            string buildHookFolder = Path.GetFullPath(Path.Combine(buildContextPath, "hooks"));
            if (!Directory.Exists(buildHookFolder))
            {
                return;
            }

            string scriptPath = Path.Combine(buildHookFolder, hookName);
            ProcessStartInfo startInfo;
            if (File.Exists(scriptPath))
            {
                startInfo = new ProcessStartInfo(scriptPath);
            }
            else
            {
                scriptPath = Path.ChangeExtension(scriptPath, ".ps1");
                if (!File.Exists(scriptPath))
                {
                    return;
                }

                startInfo = new ProcessStartInfo(
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "PowerShell" : "pwsh",
                    $"-NoProfile -File \"{scriptPath}\"");
            }

            startInfo.WorkingDirectory = buildContextPath;
            ExecuteHelper.Execute(startInfo, Options.IsDryRun, $"Failed to execute build hook '{scriptPath}'");
        }

        private void PullBaseImages()
        {
            if (!Options.IsSkipPullingEnabled)
            {
                DockerHelper.PullBaseImages(Manifest, Options);
            }
        }

        private void PushImages()
        {
            if (Options.IsPushEnabled)
            {
                Logger.WriteHeading("PUSHING IMAGES");

                ExecuteWithUser(() =>
                {
                    IEnumerable<string> pushTags = BuiltTags
                        .Where(tag => !tag.Model.IsLocal)
                        .Select(tag => tag.FullyQualifiedName);
                    foreach (string tag in pushTags)
                    {
                        ExecuteHelper.ExecuteWithRetry("docker", $"push {tag}", Options.IsDryRun);
                    }
                });
            }
        }

        private bool UpdateDockerfileFromCommands(PlatformInfo platform, out string dockerfilePath)
        {
            bool updateDockerfile = false;
            dockerfilePath = platform.DockerfilePath;

            // If a repo override has been specified, update the FROM commands.
            if (platform.OverriddenFromImages.Any())
            {
                string dockerfileContents = File.ReadAllText(dockerfilePath);

                foreach (string fromImage in platform.OverriddenFromImages)
                {
                    string fromRepo = DockerHelper.GetRepo(fromImage);
                    RepoInfo repo = Manifest.FilteredRepos.First(r => r.Model.Name == fromRepo);
                    string newFromImage = DockerHelper.ReplaceRepo(fromImage, repo.Name);
                    Logger.WriteMessage($"Replacing FROM `{fromImage}` with `{newFromImage}`");
                    Regex fromRegex = new Regex($@"FROM\s+{Regex.Escape(fromImage)}[^\S\r\n]*");
                    dockerfileContents = fromRegex.Replace(dockerfileContents, $"FROM {newFromImage}");
                    updateDockerfile = true;
                }

                if (updateDockerfile)
                {
                    // Don't overwrite the original dockerfile - write it to a new path.
                    dockerfilePath += ".temp";
                    Logger.WriteMessage($"Writing updated Dockerfile: {dockerfilePath}");
                    Logger.WriteMessage(dockerfileContents);
                    File.WriteAllText(dockerfilePath, dockerfileContents);
                }
            }

            return updateDockerfile;
        }

        private void WriteBuildSummary()
        {
            Logger.WriteHeading("IMAGES BUILT");

            if (BuiltTags.Any())
            {
                foreach (string tag in BuiltTags.Select(tag => tag.FullyQualifiedName))
                {
                    Logger.WriteMessage(tag);
                }
            }
            else
            {
                Logger.WriteMessage("No images built");
            }

            Logger.WriteMessage();
        }
    }
}
