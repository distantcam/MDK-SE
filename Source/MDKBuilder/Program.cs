using Malware.MDKServices;
using MDK.Build;
using MDK.Build.Composers;
using MDK.Build.Composers.Default;
using MDK.Build.Composers.Minifying;
using MDK.Build.Solution;
using MDK.Build.TypeTrimming;
using MDK.Resources;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System;
using System.Collections;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MDKBuilder
{
    class Program
    {
        static async Task Main(string[] args)
        {
            MSBuildLocator.RegisterDefaults();

            var rootCommand = new RootCommand
            {
                new Argument<IEnumerable<string>>("files")
            };

            rootCommand.Handler = CommandHandler.Create((IEnumerable<string> files) => Process(files));

            await rootCommand.InvokeAsync(args);
        }

        static async Task Process(IEnumerable<string> files)
        {
            var workspace = MSBuildWorkspace.Create();
            foreach (var file in files)
            {
                var project = await workspace.OpenProjectAsync(file);

                var builtScript = await BuildAsync(project);
            }
        }

        static async Task<MDKProjectProperties> BuildAsync(Project project)
        {
            var config = LoadConfig(project);
            if (!config.IsValid)
                return null;

            var composition = await ComposeDocumentAsync(project, config);

            if (config.Options.TrimTypes)
            {
                var processor = new TypeTrimmer();
                composition = await processor.ProcessAsync(composition, config);
            }
            ScriptComposer composer;
            switch (config.Options.MinifyLevel)
            {
                case MinifyLevel.Full: composer = new MinifyingComposer(); break;
                case MinifyLevel.StripComments: composer = new StripCommentsComposer(); break;
                case MinifyLevel.Lite: composer = new LiteComposer(); break;
                default: composer = new DefaultComposer(); break;
            }
            var script = await ComposeScriptAsync(composition, composer, config).ConfigureAwait(false);

            if (composition.Readme != null)
            {
                script = composition.Readme + script;
            }

            WriteScript(project, config.Paths.OutputPath, script);
            return config;
        }

        static MDKProjectProperties LoadConfig(Project project)
        {
            try
            {
                return MDKProjectProperties.Load(project.FilePath, project.Name);
            }
            catch (Exception e)
            {
                throw new BuildException(string.Format(Text.BuildModule_LoadConfig_Error, project.FilePath), e);
            }
        }

        static async Task<ProgramComposition> ComposeDocumentAsync(Project project, MDKProjectProperties config)
        {
            try
            {
                var documentComposer = new ProgramDocumentComposer();
                return await documentComposer.ComposeAsync(project, config).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw new BuildException(string.Format(Text.BuildModule_LoadContent_Error, project.FilePath), e);
            }
        }

        static async Task<string> ComposeScriptAsync(ProgramComposition composition, ScriptComposer composer, MDKProjectProperties config)
        {
            try
            {
                var script = await composer.GenerateAsync(composition, config).ConfigureAwait(false);
                return script;
            }
            catch (Exception e)
            {
                throw new BuildException(string.Format(Text.BuildModule_GenerateScript_ErrorGeneratingScript, composition.Document.Project.FilePath), e);
            }
        }

        static void WriteScript(Project project, string output, string script)
        {
            try
            {
                var outputInfo = new DirectoryInfo(ExpandMacros(project, Path.Combine(output, project.Name)));
                if (!outputInfo.Exists)
                    outputInfo.Create();
                File.WriteAllText(Path.Combine(outputInfo.FullName, "script.cs"), script.Replace("\r\n", "\n"), Encoding.UTF8);

                var thumbFile = new FileInfo(Path.Combine(Path.GetDirectoryName(project.FilePath) ?? ".", "thumb.png"));
                if (thumbFile.Exists)
                    thumbFile.CopyTo(Path.Combine(outputInfo.FullName, "thumb.png"), true);
            }
            catch (UnauthorizedAccessException e)
            {
                throw new UnauthorizedAccessException(string.Format(Text.BuildModule_WriteScript_UnauthorizedAccess, project.FilePath), e);
            }
            catch (Exception e)
            {
                throw new BuildException(string.Format(Text.BuildModule_WriteScript_Error, project.FilePath), e);
            }
        }

        static string ExpandMacros(Project project, string input)
        {
            var replacements = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase)
            {
                ["$(projectname)"] = project.Name ?? ""
            };
            foreach (DictionaryEntry envVar in Environment.GetEnvironmentVariables())
                replacements[$"%{envVar.Key}%"] = (string)envVar.Value;
            return Regex.Replace(input, @"\$\(ProjectName\)|%[^%]+%", match =>
            {
                if (replacements.TryGetValue(match.Value, out var value))
                {
                    return value;
                }

                return match.Value;
            }, RegexOptions.IgnoreCase);
        }
    }
}
