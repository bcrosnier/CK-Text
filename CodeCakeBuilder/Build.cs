﻿using Cake.Common.Build;
using Cake.Common.Diagnostics;
using Cake.Common.IO;
using Cake.Common.Solution;
using Cake.Common.Tools.DotNetCore;
using Cake.Common.Tools.DotNetCore.Build;
using Cake.Common.Tools.DotNetCore.Pack;
using Cake.Common.Tools.DotNetCore.Restore;
using Cake.Common.Tools.NuGet;
using Cake.Common.Tools.NuGet.Push;
using Cake.Common.Tools.NUnit;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Core.IO;
using SimpleGitVersion;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace CodeCake
{
    public static class DotNetCoreRestoreSettingsExtension
    {
        public static T AddVersionArguments<T>(this T @this, SimpleRepositoryInfo info, Action<T> conf = null) where T : DotNetCoreSettings
        {
            if (info.IsValid)
            {
                var prev = @this.ArgumentCustomization;
                @this.ArgumentCustomization = args => (prev?.Invoke(args) ?? args)
                        .Append($@"/p:CakeBuild=""true""")
                        .Append($@"/p:Version=""{info.NuGetVersion}""")
                        .Append($@"/p:AssemblyVersion=""{info.MajorMinor}.0""")
                        .Append($@"/p:FileVersion=""{info.FileVersion}""")
                        .Append($@"/p:InformationalVersion=""{info.SemVer} ({info.NuGetVersion}) - SHA1: {info.CommitSha} - CommitDate: {info.CommitDateUtc.ToString("u")}""");
            }
            conf?.Invoke(@this);
            return @this;
        }
    }

    /// <summary>
    /// Standard build "script".
    /// </summary>
    [AddPath( "CodeCakeBuilder/Tools" )]
    [AddPath( "packages/**/tools*" )]
    public class Build : CodeCakeHost
    {
        public Build()
        {
            Cake.Log.Verbosity = Verbosity.Diagnostic;

            const string solutionName = "CK-Text";
            const string solutionFileName = solutionName + ".sln";
            const string coreBuildProj = "CodeCakeBuilder/CoreBuild.proj";

            var releasesDir = Cake.Directory( "CodeCakeBuilder/Releases" );

            var projects = Cake.ParseSolution(solutionFileName)
                                       .Projects
                                       .Where(p => !(p is SolutionFolder)
                                                   && p.Name != "CodeCakeBuilder" );

            // We do not publish .Tests projects for this solution.
            var projectsToPublish = projects
                                        .Where( p => !p.Path.Segments.Contains( "Tests" ) );

            SimpleRepositoryInfo gitInfo = Cake.GetSimpleRepositoryInfo();

            // Configuration is either "Debug" or "Release".
            string configuration = "Debug";

            Task( "Check-Repository" )
                .Does( () =>
                {
                    if (!gitInfo.IsValid)
                    {
                        if (Cake.IsInteractiveMode()
                            && Cake.ReadInteractiveOption("Repository is not ready to be published. Proceed anyway?", 'Y', 'N') == 'Y')
                        {
                            Cake.Warning("GitInfo is not valid, but you choose to continue...");
                        }
                        else throw new Exception("Repository is not ready to be published.");
                    }

                    if( gitInfo.IsValidRelease 
                        && (gitInfo.PreReleaseName.Length == 0 || gitInfo.PreReleaseName == "rc") )
                    {
                        configuration = "Release";
                    }

                    Cake.Information( "Publishing {0} projects with version={1} and configuration={2}: {3}",
                        projectsToPublish.Count(),
                        gitInfo.SemVer,
                        configuration,
                        string.Join( ", ", projectsToPublish.Select( p => p.Name ) ) );
                } );

            Task( "Clean" )
                .IsDependentOn( "Check-Repository" )
                .Does( () =>
                {
                    Cake.CleanDirectories(projects.Select(p => p.Path.GetDirectory().Combine("bin")));
                    Cake.CleanDirectories( releasesDir );
                    Cake.DeleteFiles( "Tests/**/TestResult*.xml" );
                } );

            Task( "Restore-NuGet-Packages" )
                .IsDependentOn("Check-Repository")
                .IsDependentOn("Clean")
                .Does( () =>
                {
                    // https://docs.microsoft.com/en-us/nuget/schema/msbuild-targets
                    Cake.DotNetCoreRestore( coreBuildProj, new DotNetCoreRestoreSettings().AddVersionArguments( gitInfo ) );
                } );

            Task("Build")
                .IsDependentOn("Check-Repository")
                .IsDependentOn("Clean")
                .IsDependentOn("Restore-NuGet-Packages")
                .Does(() =>
               {
                   Cake.DotNetCoreBuild(coreBuildProj,
                       new DotNetCoreBuildSettings().AddVersionArguments(gitInfo, s =>
                       {
                           s.Configuration = configuration;
                       }));
               });

            Task( "Unit-Testing" )
                .IsDependentOn( "Build" )
                .Does( () =>
                {
                    Cake.CreateDirectory( releasesDir );
                    var testDlls = projects.Where(p => p.Name.EndsWith(".Tests")).Select(p =>
                            new
                            {
                                ProjectPath = p.Path.GetDirectory(),
                                NetCoreAppDll = p.Path.GetDirectory().CombineWithFilePath("bin/" + configuration + "/netcoreapp1.1/" + p.Name + ".dll"),
                                Net451Exe = p.Path.GetDirectory().CombineWithFilePath("bin/" + configuration + "/net451/" + p.Name + ".exe"),
                            });

                    foreach (var test in testDlls)
                    {
                        using (Cake.Environment.SetWorkingDirectory(test.ProjectPath))
                        {
                            Cake.Information("Testing: {0}", test.Net451Exe);
                            Cake.NUnit(test.Net451Exe.FullPath, new NUnitSettings()
                            {
                                Framework = "v4.5",
                                ResultsFile = test.ProjectPath.CombineWithFilePath("TestResult.Net451.xml")
                            });
                            Cake.Information("Testing: {0}", test.NetCoreAppDll);
                            Cake.DotNetCoreExecute(test.NetCoreAppDll);
                        }
                    }
                });

            Task( "Create-NuGet-Packages" )
                .WithCriteria( () => gitInfo.IsValid )
                .IsDependentOn( "Unit-Testing" )
                .Does( () =>
                {
                    Cake.CreateDirectory( releasesDir );
                    foreach( SolutionProject p in projectsToPublish )
                    {
                        Cake.Warning(p.Path.GetDirectory().FullPath);
                        var s = new DotNetCorePackSettings();
                        s.ArgumentCustomization = args => args.Append("--include-symbols");
                        s.NoBuild = true;
                        s.Configuration = configuration;
                        s.OutputDirectory = releasesDir;
                        s.AddVersionArguments(gitInfo);
                        Cake.DotNetCorePack( p.Path.GetDirectory().FullPath, s );
                    }
                } );


            Task( "Push-NuGet-Packages" )
                .IsDependentOn( "Create-NuGet-Packages" )
                .WithCriteria( () => gitInfo.IsValid )
                .Does( () =>
                {
                    IEnumerable<FilePath> nugetPackages = Cake.GetFiles( releasesDir.Path + "/*.nupkg" );
                    if( Cake.IsInteractiveMode() )
                    {
                        var localFeed = Cake.FindDirectoryAbove( "LocalFeed" );
                        if( localFeed != null )
                        {
                            Cake.Information( "LocalFeed directory found: {0}", localFeed );
                            if( Cake.ReadInteractiveOption( "Do you want to publish to LocalFeed?", 'Y', 'N' ) == 'Y' )
                            {
                                Cake.CopyFiles( nugetPackages, localFeed );
                            }
                        }
                    }
                    if( gitInfo.IsValidRelease )
                    {
                        if( gitInfo.PreReleaseName == "" 
                            || gitInfo.PreReleaseName == "prerelease" 
                            || gitInfo.PreReleaseName == "rc" )
                        {
                            PushNuGetPackages( "NUGET_API_KEY", "https://www.nuget.org/api/v2/package", nugetPackages );
                        }
                        else
                        {
                            // An alpha, beta, delta, epsilon, gamma, kappa goes to invenietis-preview.
                            PushNuGetPackages( "MYGET_PREVIEW_API_KEY", "https://www.myget.org/F/invenietis-preview/api/v2/package", nugetPackages );
                        }
                    }
                    else
                    {
                        Debug.Assert( gitInfo.IsValidCIBuild );
                        PushNuGetPackages( "MYGET_CI_API_KEY", "https://www.myget.org/F/invenietis-ci/api/v2/package", nugetPackages );
                    }
                    if (Cake.AppVeyor().IsRunningOnAppVeyor)
                    {
                        Cake.AppVeyor().UpdateBuildVersion(gitInfo.SemVer);
                    }
                });

            // The Default task for this script can be set here.
            Task( "Default" )
                .IsDependentOn( "Push-NuGet-Packages" );

        }

        void PushNuGetPackages( string apiKeyName, string pushUrl, IEnumerable<FilePath> nugetPackages )
        {
            // Resolves the API key.
            var apiKey = Cake.InteractiveEnvironmentVariable( apiKeyName );
            if( string.IsNullOrEmpty( apiKey ) )
            {
                Cake.Information( "Could not resolve {0}. Push to {1} is skipped.", apiKeyName, pushUrl );
            }
            else
            {
                var settings = new NuGetPushSettings
                {
                    Source = pushUrl,
                    ApiKey = apiKey,
                    Verbosity = NuGetVerbosity.Detailed
                };

                foreach( var nupkg in nugetPackages.Where( p => !p.FullPath.EndsWith(".symbols.nupkg") ) )
                {
                    Cake.Information($"Pushing '{nupkg}' to '{pushUrl}'.");
                    Cake.NuGetPush( nupkg, settings );
                }
            }
        }
    }
}