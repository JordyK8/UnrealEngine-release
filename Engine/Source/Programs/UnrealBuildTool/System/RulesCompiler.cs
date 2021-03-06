// Copyright Epic Games, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Tools.DotNETCommon;

namespace UnrealBuildTool
{
	/// <summary>
	/// Class which compiles (and caches) rules assemblies for different folders.
	/// </summary>
	public class RulesCompiler
	{
		/// <summary>
		/// Enum for types of rules files. Should match extensions in RulesFileExtensions.
		/// </summary>
		public enum RulesFileType
		{
			/// <summary>
			/// .build.cs files
			/// </summary>
			Module,

			/// <summary>
			/// .target.cs files
			/// </summary>
			Target,

			/// <summary>
			/// .automation.csproj files
			/// </summary>
			AutomationModule
		}

		/// <summary>
		/// Cached list of rules files in each directory of each type
		/// </summary>
		class RulesFileCache
		{
			public List<FileReference> ModuleRules = new List<FileReference>();
			public List<FileReference> TargetRules = new List<FileReference>();
			public List<FileReference> AutomationModules = new List<FileReference>();
		}

		/// Map of root folders to a cached list of all UBT-related source files in that folder or any of its sub-folders.
		/// We cache these file names so we can avoid searching for them later on.
		static Dictionary<DirectoryReference, RulesFileCache> RootFolderToRulesFileCache = new Dictionary<DirectoryReference, RulesFileCache>();

		/// <summary>
		/// 
		/// </summary>
#if NET_CORE
		const string FrameworkAssemblyExtension = "_NetCore.dll";
#else
		const string FrameworkAssemblyExtension = ".dll";
#endif

		/// <summary>
		/// 
		/// </summary>
		/// <param name="RulesFileType"></param>
		/// <param name="GameFolders"></param>
		/// <param name="ForeignPlugins"></param>
		/// <param name="AdditionalSearchPaths"></param>
		/// <param name="bIncludeEngine"></param>
		/// <param name="bIncludeEnterprise"></param>
		/// <param name="bIncludeTempTargets">Whether to include targets generated by UAT to accomodate content-only projects that need to be compiled to include plugins</param>
		/// <returns></returns>
		public static List<FileReference> FindAllRulesSourceFiles(RulesFileType RulesFileType, List<DirectoryReference> GameFolders, List<FileReference> ForeignPlugins, List<DirectoryReference> AdditionalSearchPaths, bool bIncludeEngine = true, bool bIncludeEnterprise = true, bool bIncludeTempTargets = true)
		{
			List<DirectoryReference> Folders = new List<DirectoryReference>();

			// Add all engine source (including third party source)
			if (bIncludeEngine)
			{
				Folders.AddRange(UnrealBuildTool.GetExtensionDirs(UnrealBuildTool.EngineDirectory, "Source"));
			}
			if(bIncludeEnterprise)
			{
				Folders.Add(UnrealBuildTool.EnterpriseSourceDirectory);
			}

			// @todo plugin: Disallow modules from including plugin modules as dependency modules? (except when the module is part of that plugin)

			// Get all the root folders for plugins
			List<DirectoryReference> RootFolders = new List<DirectoryReference>();
			if (bIncludeEngine)
			{
				RootFolders.AddRange(UnrealBuildTool.GetExtensionDirs(UnrealBuildTool.EngineDirectory));
			}
			if(bIncludeEnterprise)
			{
				RootFolders.Add(UnrealBuildTool.EnterpriseDirectory);
			}
			if (GameFolders != null)
			{
				RootFolders.AddRange(GameFolders.SelectMany(x => UnrealBuildTool.GetExtensionDirs(x)));
			}

			// Find all the plugin source directories
			foreach (DirectoryReference RootFolder in RootFolders)
			{
				DirectoryReference PluginsFolder = DirectoryReference.Combine(RootFolder, "Plugins");
				foreach (FileReference PluginFile in Plugins.EnumeratePlugins(PluginsFolder))
				{
					Folders.Add(DirectoryReference.Combine(PluginFile.Directory, "Source"));
				}
			}

			// Add all the extra plugin folders
			if (ForeignPlugins != null)
			{
				foreach (FileReference ForeignPlugin in ForeignPlugins)
				{
					Folders.Add(DirectoryReference.Combine(ForeignPlugin.Directory, "Source"));
				}
			}

			// Add in the game folders to search
			if (GameFolders != null)
			{
				foreach (DirectoryReference GameFolder in GameFolders)
				{
					Folders.AddRange(UnrealBuildTool.GetExtensionDirs(GameFolder, "Source"));

					if (bIncludeTempTargets)
					{
						DirectoryReference GameIntermediateSourceFolder = DirectoryReference.Combine(GameFolder, "Intermediate", "Source");
						Folders.Add(GameIntermediateSourceFolder);
					}
				}
			}

			// Process the additional search path, if sent in
			if (AdditionalSearchPaths != null)
			{
				foreach (DirectoryReference AdditionalSearchPath in AdditionalSearchPaths)
				{
					if (AdditionalSearchPath != null)
					{
						if (DirectoryReference.Exists(AdditionalSearchPath))
						{
							Folders.Add(AdditionalSearchPath);
						}
						else
						{
							throw new BuildException("Couldn't find AdditionalSearchPath for rules source files '{0}'", AdditionalSearchPath);
						}
					}
				}
			}

			// Iterate over all the folders to check
			List<FileReference> SourceFiles = new List<FileReference>();
			HashSet<FileReference> UniqueSourceFiles = new HashSet<FileReference>();
			foreach (DirectoryReference Folder in Folders)
			{
				IReadOnlyList<FileReference> SourceFilesForFolder = FindAllRulesFiles(Folder, RulesFileType);
				foreach (FileReference SourceFile in SourceFilesForFolder)
				{
					if (UniqueSourceFiles.Add(SourceFile))
					{
						SourceFiles.Add(SourceFile);
					}
				}
			}
			return SourceFiles;
		}

		/// <summary>
		/// Invalidate the cache for the givcen directory
		/// </summary>
		/// <param name="DirectoryPath">Directory to invalidate</param>
        public static void InvalidateRulesFileCache(string DirectoryPath)
        {
            DirectoryReference Directory = new DirectoryReference(DirectoryPath);
            RootFolderToRulesFileCache.Remove(Directory);
            DirectoryLookupCache.InvalidateCachedDirectory(Directory);
        }

		/// <summary>
		/// Prefetch multiple directories in parallel
		/// </summary>
		/// <param name="Directories">The directories to cache</param>
		private static void PrefetchRulesFiles(IEnumerable<DirectoryReference> Directories)
		{
			ThreadPoolWorkQueue Queue = null;
			try
			{
				foreach(DirectoryReference Directory in Directories)
				{
					if(!RootFolderToRulesFileCache.ContainsKey(Directory))
					{
						RulesFileCache Cache = new RulesFileCache();
						RootFolderToRulesFileCache[Directory] = Cache;

						if(Queue == null)
						{
							Queue = new ThreadPoolWorkQueue();
						}

						DirectoryItem DirectoryItem = DirectoryItem.GetItemByDirectoryReference(Directory);
						Queue.Enqueue(() => FindAllRulesFilesRecursively(DirectoryItem, Cache, Queue));
					}
				}
			}
			finally
			{
				if(Queue != null)
				{
					Queue.Dispose();
					Queue = null;
				}
			}
		}

		/// <summary>
		/// Finds all the rules of the given type under a given directory
		/// </summary>
		/// <param name="Directory">Directory to search</param>
		/// <param name="Type">Type of rules to return</param>
		/// <returns>List of rules files of the given type</returns>
		private static IReadOnlyList<FileReference> FindAllRulesFiles(DirectoryReference Directory, RulesFileType Type)
		{
			// Check to see if we've already cached source files for this folder
			RulesFileCache Cache;
			if (!RootFolderToRulesFileCache.TryGetValue(Directory, out Cache))
			{
				Cache = new RulesFileCache();
				using(ThreadPoolWorkQueue Queue = new ThreadPoolWorkQueue())
				{
					DirectoryItem BaseDirectory = DirectoryItem.GetItemByDirectoryReference(Directory);
					Queue.Enqueue(() => FindAllRulesFilesRecursively(BaseDirectory, Cache, Queue));
				}
				Cache.ModuleRules.Sort((A, B) => A.FullName.CompareTo(B.FullName));
				Cache.TargetRules.Sort((A, B) => A.FullName.CompareTo(B.FullName));
				Cache.AutomationModules.Sort((A, B) => A.FullName.CompareTo(B.FullName));
				RootFolderToRulesFileCache[Directory] = Cache;
			}

			// Get the list of files of the type we're looking for
			if (Type == RulesCompiler.RulesFileType.Module)
			{
				return Cache.ModuleRules;
			}
			else if (Type == RulesCompiler.RulesFileType.Target)
			{
				return Cache.TargetRules;
			}
			else if (Type == RulesCompiler.RulesFileType.AutomationModule)
			{
				return Cache.AutomationModules;
			}
			else
			{
				throw new BuildException("Unhandled rules type: {0}", Type);
			}
		}

		/// <summary>
		/// Search through a directory tree for any rules files
		/// </summary>
		/// <param name="Directory">The root directory to search from</param>
		/// <param name="Cache">Receives all the discovered rules files</param>
		/// <param name="Queue">Queue for adding additional tasks to</param>
		private static void FindAllRulesFilesRecursively(DirectoryItem Directory, RulesFileCache Cache, ThreadPoolWorkQueue Queue)
		{
			// Scan all the files in this directory
			bool bSearchSubFolders = true;
			foreach (FileItem File in Directory.EnumerateFiles())
			{
				if (File.HasExtension(".build.cs"))
				{
					lock(Cache.ModuleRules)
					{
						Cache.ModuleRules.Add(File.Location);
					}
					bSearchSubFolders = false;
				}
				else if (File.HasExtension(".target.cs"))
				{
					lock(Cache.TargetRules)
					{
						Cache.TargetRules.Add(File.Location);
					}
				}
				else if (File.HasExtension(".automation.csproj"))
				{
					lock(Cache.AutomationModules)
					{
						Cache.AutomationModules.Add(File.Location);
					}
					bSearchSubFolders = false;
				}
			}

			// If we didn't find anything to stop the search, search all the subdirectories too
			if (bSearchSubFolders)
			{
				foreach (DirectoryItem SubDirectory in Directory.EnumerateDirectories())
				{
					Queue.Enqueue(() => FindAllRulesFilesRecursively(SubDirectory, Cache, Queue));
				}
			}
		}

		/// <summary>
		/// Find all the module rules files under a given directory
		/// </summary>
		/// <param name="BaseDirectory">The directory to search under</param>
		/// <param name="ModuleContext">The module context for each found rules instance</param>
		/// <param name="ModuleFileToContext">Map of module files to their context</param>
		private static void AddModuleRulesWithContext(DirectoryReference BaseDirectory, ModuleRulesContext ModuleContext, Dictionary<FileReference, ModuleRulesContext> ModuleFileToContext)
		{
			IReadOnlyList<FileReference> RulesFiles = FindAllRulesFiles(BaseDirectory, RulesFileType.Module);
			foreach (FileReference RulesFile in RulesFiles)
			{
				ModuleFileToContext[RulesFile] = ModuleContext;
			}
		}

		/// <summary>
		/// Find all the module rules files under a given directory
		/// </summary>
		/// <param name="BaseDirectory">The directory to search under</param>
		/// <param name="SubDirectoryName">Name of the subdirectory to look under</param>
		/// <param name="BaseModuleContext">The module context for each found rules instance</param>
		/// <param name="DefaultUHTModuleType">The UHT module type</param>
		/// <param name="ModuleFileToContext">Map of module files to their context</param>
		private static void AddEngineModuleRulesWithContext(DirectoryReference BaseDirectory, string SubDirectoryName, ModuleRulesContext BaseModuleContext, UHTModuleType? DefaultUHTModuleType, Dictionary<FileReference, ModuleRulesContext> ModuleFileToContext)
		{
			DirectoryReference Directory = DirectoryReference.Combine(BaseDirectory, SubDirectoryName);
			if (DirectoryLookupCache.DirectoryExists(Directory))
			{
				ModuleRulesContext ModuleContext = new ModuleRulesContext(BaseModuleContext) { DefaultUHTModuleType = DefaultUHTModuleType };
				AddModuleRulesWithContext(Directory, ModuleContext, ModuleFileToContext);
			}
		}

		/// <summary>
		/// The cached rules assembly for engine modules and targets.
		/// </summary>
		private static RulesAssembly EngineRulesAssembly;

		/// <summary>
		/// The cached rules assembly for enterprise modules and targets.
		/// </summary>
		private static RulesAssembly EnterpriseRulesAssembly;

		/// <summary>
		/// Map of assembly names we've already compiled and loaded to their Assembly and list of game folders.  This is used to prevent
		/// trying to recompile the same assembly when ping-ponging between different types of targets
		/// </summary>
		private static Dictionary<FileReference, RulesAssembly> LoadedAssemblyMap = new Dictionary<FileReference, RulesAssembly>();

		/// <summary>
		/// Creates the engine rules assembly
		/// </summary>
		/// <param name="bUsePrecompiled">Whether to use a precompiled engine</param>
		/// <param name="bSkipCompile">Whether to skip compilation for this assembly</param>
		/// <returns>New rules assembly</returns>
		public static RulesAssembly CreateEngineRulesAssembly(bool bUsePrecompiled, bool bSkipCompile)
		{
			if (EngineRulesAssembly == null)
			{
				List<PluginInfo> IncludedPlugins = new List<PluginInfo>();

				// search for all engine plugins
				IncludedPlugins.AddRange(Plugins.ReadEnginePlugins(UnrealBuildTool.EngineDirectory));

				RulesScope EngineScope = new RulesScope("Engine", null);

				EngineRulesAssembly = CreateEngineOrEnterpriseRulesAssembly(EngineScope, UnrealBuildTool.GetExtensionDirs(UnrealBuildTool.EngineDirectory), ProjectFileGenerator.EngineProjectFileNameBase, IncludedPlugins, UnrealBuildTool.IsEngineInstalled() || bUsePrecompiled, bSkipCompile, null);
			}
			return EngineRulesAssembly;
		}

		/// <summary>
		/// Creates the enterprise rules assembly
		/// </summary>
		/// <param name="bUsePrecompiled">Whether to use a precompiled enterprise and engine folder</param>
		/// <param name="bSkipCompile">Whether to skip compilation for this assembly</param>
		/// <returns>New rules assembly. Returns null if the enterprise directory is unavailable.</returns>
		public static RulesAssembly CreateEnterpriseRulesAssembly(bool bUsePrecompiled, bool bSkipCompile)
		{
			if (EnterpriseRulesAssembly == null)
			{
				RulesAssembly EngineAssembly = CreateEngineRulesAssembly(bUsePrecompiled, bSkipCompile);
				if (DirectoryReference.Exists(UnrealBuildTool.EnterpriseDirectory))
				{
					RulesScope EnterpriseScope = new RulesScope("Enterprise", EngineAssembly.Scope);

					//List<DirectoryReference> EnterpriseDirectories = new List<DirectoryReference>() { UnrealBuildTool.EnterpriseDirectory };

					IReadOnlyList<PluginInfo> IncludedPlugins = Plugins.ReadEnterprisePlugins(UnrealBuildTool.EnterpriseDirectory);
					EnterpriseRulesAssembly = CreateEngineOrEnterpriseRulesAssembly(EnterpriseScope, new List<DirectoryReference>() { UnrealBuildTool.EnterpriseDirectory }, 
						ProjectFileGenerator.EnterpriseProjectFileNameBase, IncludedPlugins, UnrealBuildTool.IsEnterpriseInstalled() || bUsePrecompiled, bSkipCompile, EngineAssembly);
				}
				else
				{
					// If we're asked for the enterprise rules assembly but the enterprise directory is missing, fallback on the engine rules assembly
					Log.TraceWarning("Trying to build an enterprise target but the enterprise directory is missing. Falling back on engine components only.");
					return EngineAssembly;
				}
			}

			return EnterpriseRulesAssembly;
		}

		/// <summary>
		/// Creates a rules assembly
		/// </summary>
		/// <param name="Scope">Scope for items created from this assembly</param>
		/// <param name="RootDirectories">The root directories to create rules for</param>
		/// <param name="AssemblyPrefix">A prefix for the assembly file name</param>
		/// <param name="Plugins">List of plugins to include in this assembly</param>
		/// <param name="bReadOnly">Whether the assembly should be marked as installed</param>
		/// <param name="bSkipCompile">Whether to skip compilation for this assembly</param>
		/// <param name="Parent">The parent rules assembly</param>
		/// <returns>New rules assembly</returns>
		private static RulesAssembly CreateEngineOrEnterpriseRulesAssembly(RulesScope Scope, List<DirectoryReference> RootDirectories, string AssemblyPrefix, IReadOnlyList<PluginInfo> Plugins, bool bReadOnly, bool bSkipCompile, RulesAssembly Parent)
		{
			// Scope hierarchy
			RulesScope PluginsScope = new RulesScope(Scope.Name + " Plugins", Scope);
			RulesScope ProgramsScope = new RulesScope(Scope.Name + " Programs", PluginsScope);

			// Find the shared modules, excluding the programs directory. These are used to create an assembly with the bContainsEngineModules flag set to true.
			Dictionary<FileReference, ModuleRulesContext> ModuleFileToContext = new Dictionary<FileReference, ModuleRulesContext>();
			ModuleRulesContext DefaultModuleContext = new ModuleRulesContext(Scope, RootDirectories[0]);

			foreach (DirectoryReference RootDirectory in RootDirectories)
			{
				using (Timeline.ScopeEvent("Finding engine modules"))
				{
					DirectoryReference SourceDirectory = DirectoryReference.Combine(RootDirectory, "Source");

					AddEngineModuleRulesWithContext(SourceDirectory, "Runtime", DefaultModuleContext, UHTModuleType.EngineRuntime, ModuleFileToContext);
					AddEngineModuleRulesWithContext(SourceDirectory, "Developer", DefaultModuleContext, UHTModuleType.EngineDeveloper, ModuleFileToContext);
					AddEngineModuleRulesWithContext(SourceDirectory, "Editor", DefaultModuleContext, UHTModuleType.EngineEditor, ModuleFileToContext);
					AddEngineModuleRulesWithContext(SourceDirectory, "ThirdParty", DefaultModuleContext, UHTModuleType.EngineThirdParty, ModuleFileToContext);
				}
			}

			// Add all the plugin modules too (don't need to loop over RootDirectories since the plugins come in already found
			using (Timeline.ScopeEvent("Finding plugin modules"))
			{
				ModuleRulesContext PluginsModuleContext = new ModuleRulesContext(PluginsScope, RootDirectories[0]);
				FindModuleRulesForPlugins(Plugins, PluginsModuleContext, ModuleFileToContext);
			}

			// Create the assembly
			DirectoryReference AssemblyDir = RootDirectories[0];
			FileReference EngineAssemblyFileName = FileReference.Combine(AssemblyDir, "Intermediate", "Build", "BuildRules", AssemblyPrefix + "Rules" + FrameworkAssemblyExtension);
			RulesAssembly EngineAssembly = new RulesAssembly(Scope, RootDirectories, Plugins, ModuleFileToContext, new List<FileReference>(), EngineAssemblyFileName, bContainsEngineModules: true, DefaultBuildSettings: BuildSettingsVersion.Latest, bReadOnly: bReadOnly, bSkipCompile: bSkipCompile, Parent: Parent);

			List<FileReference> ProgramTargetFiles = new List<FileReference>();
			Dictionary<FileReference, ModuleRulesContext> ProgramModuleFiles = new Dictionary<FileReference, ModuleRulesContext>();
			foreach (DirectoryReference RootDirectory in RootDirectories)
			{
				DirectoryReference SourceDirectory = DirectoryReference.Combine(RootDirectory, "Source");
				DirectoryReference ProgramsDirectory = DirectoryReference.Combine(SourceDirectory, "Programs");

				// Also create a scope for them, and update the UHT module type
				ModuleRulesContext ProgramsModuleContext = new ModuleRulesContext(ProgramsScope, RootDirectory);
				ProgramsModuleContext.DefaultUHTModuleType = UHTModuleType.Program;

				using (Timeline.ScopeEvent("Finding program modules"))
				{
					// Find all the rules files
					AddModuleRulesWithContext(ProgramsDirectory, ProgramsModuleContext, ProgramModuleFiles);
				}

				using (Timeline.ScopeEvent("Finding program targets"))
				{
					ProgramTargetFiles.AddRange(FindAllRulesFiles(SourceDirectory, RulesFileType.Target));
				}
			}

			// Create a path to the assembly that we'll either load or compile
			FileReference ProgramAssemblyFileName = FileReference.Combine(AssemblyDir, "Intermediate", "Build", "BuildRules", AssemblyPrefix + "ProgramRules" + FrameworkAssemblyExtension);
			RulesAssembly ProgramAssembly = new RulesAssembly(ProgramsScope, RootDirectories, new List<PluginInfo>().AsReadOnly(), ProgramModuleFiles, ProgramTargetFiles, ProgramAssemblyFileName, bContainsEngineModules: false, DefaultBuildSettings: BuildSettingsVersion.Latest, bReadOnly: bReadOnly, bSkipCompile: bSkipCompile, Parent: EngineAssembly);

			// Return the combined assembly
			return ProgramAssembly;
		}

		/// <summary>
		/// Creates a rules assembly with the given parameters.
		/// </summary>
		/// <param name="ProjectFileName">The project file to create rules for. Null for the engine.</param>
		/// <param name="bUsePrecompiled">Whether to use a precompiled engine</param>
		/// <param name="bSkipCompile">Whether to skip compilation for this assembly</param>
		/// <returns>New rules assembly</returns>
		public static RulesAssembly CreateProjectRulesAssembly(FileReference ProjectFileName, bool bUsePrecompiled, bool bSkipCompile)
		{
			// Check if there's an existing assembly for this project
			RulesAssembly ProjectRulesAssembly;
			if (!LoadedAssemblyMap.TryGetValue(ProjectFileName, out ProjectRulesAssembly))
			{
				ProjectDescriptor Project = ProjectDescriptor.FromFile(ProjectFileName);

				// Create the parent assembly
				RulesAssembly Parent;
				if (Project.IsEnterpriseProject)
				{
					Parent = CreateEnterpriseRulesAssembly(bUsePrecompiled, bSkipCompile);
				}
				else
				{
					Parent = CreateEngineRulesAssembly(bUsePrecompiled, bSkipCompile);
				}

				DirectoryReference MainProjectDirectory = ProjectFileName.Directory;
				//DirectoryReference MainProjectSourceDirectory = DirectoryReference.Combine(MainProjectDirectory, "Source");

				// Create a scope for things in this assembly
				RulesScope Scope = new RulesScope("Project", Parent.Scope);

				// Create a new context for modules created by this assembly
				ModuleRulesContext DefaultModuleContext = new ModuleRulesContext(Scope, MainProjectDirectory);
				DefaultModuleContext.bCanBuildDebugGame = true;
				DefaultModuleContext.bCanHotReload = true;
				DefaultModuleContext.bClassifyAsGameModuleForUHT = true;
				DefaultModuleContext.bCanUseForSharedPCH = false;

				// gather modules from project and platforms
				Dictionary<FileReference, ModuleRulesContext> ModuleFiles = new Dictionary<FileReference, ModuleRulesContext>();
				List<FileReference> TargetFiles = new List<FileReference>();

				// Find all the project directories
				List<DirectoryReference> ProjectDirectories = UnrealBuildTool.GetExtensionDirs(ProjectFileName.Directory);
				if (Project.AdditionalRootDirectories != null)
				{
					ProjectDirectories.AddRange(Project.AdditionalRootDirectories);
				}

				// Find all the rules/plugins under the project source directories
				foreach (DirectoryReference ProjectDirectory in ProjectDirectories)
				{
					DirectoryReference ProjectSourceDirectory = DirectoryReference.Combine(ProjectDirectory, "Source");

					AddModuleRulesWithContext(ProjectSourceDirectory, DefaultModuleContext, ModuleFiles);
					TargetFiles.AddRange(FindAllRulesFiles(ProjectSourceDirectory, RulesFileType.Target));
				}

				// Find all the project plugins
				List<PluginInfo> ProjectPlugins = new List<PluginInfo>();
				ProjectPlugins.AddRange(Plugins.ReadProjectPlugins(MainProjectDirectory));

				// Add the project's additional plugin directories plugins too
				if (Project.AdditionalPluginDirectories != null)
				{
					foreach (DirectoryReference AdditionalPluginDirectory in Project.AdditionalPluginDirectories)
					{
						ProjectPlugins.AddRange(Plugins.ReadAdditionalPlugins(AdditionalPluginDirectory));
					}
				}

				// Find all the plugin module rules
				FindModuleRulesForPlugins(ProjectPlugins, DefaultModuleContext, ModuleFiles);

				// Add the games project's intermediate source folder
				DirectoryReference ProjectIntermediateSourceDirectory = DirectoryReference.Combine(MainProjectDirectory, "Intermediate", "Source");
				if (DirectoryReference.Exists(ProjectIntermediateSourceDirectory))
				{
					AddModuleRulesWithContext(ProjectIntermediateSourceDirectory, DefaultModuleContext, ModuleFiles);
					TargetFiles.AddRange(FindAllRulesFiles(ProjectIntermediateSourceDirectory, RulesFileType.Target));
				}

				// Compile the assembly. If there are no module or target files, just use the parent assembly.
				FileReference AssemblyFileName = FileReference.Combine(MainProjectDirectory, "Intermediate", "Build", "BuildRules", ProjectFileName.GetFileNameWithoutExtension() + "ModuleRules" + FrameworkAssemblyExtension);
				if(ModuleFiles.Count == 0 && TargetFiles.Count == 0)
				{
					ProjectRulesAssembly = Parent;
				}
				else
				{
					ProjectRulesAssembly = new RulesAssembly(Scope, new List<DirectoryReference> { MainProjectDirectory }, ProjectPlugins, ModuleFiles, TargetFiles, AssemblyFileName, bContainsEngineModules: false, DefaultBuildSettings: null, bReadOnly: UnrealBuildTool.IsProjectInstalled(), bSkipCompile: bSkipCompile, Parent: Parent);
				}
				LoadedAssemblyMap.Add(ProjectFileName, ProjectRulesAssembly);
			}
			return ProjectRulesAssembly;
		}

		/// <summary>
		/// Creates a rules assembly with the given parameters.
		/// </summary>
		/// <param name="PluginFileName">The plugin file to create rules for</param>
		/// <param name="bSkipCompile">Whether to skip compilation for this assembly</param>
		/// <param name="Parent">The parent rules assembly</param>
        /// <param name="bContainsEngineModules">Whether the plugin contains engine modules. Used to initialize the default value for ModuleRules.bTreatAsEngineModule.</param>
		/// <returns>The new rules assembly</returns>
        public static RulesAssembly CreatePluginRulesAssembly(FileReference PluginFileName, bool bSkipCompile, RulesAssembly Parent, bool bContainsEngineModules)
		{
			// Check if there's an existing assembly for this project
			RulesAssembly PluginRulesAssembly;
			if (!LoadedAssemblyMap.TryGetValue(PluginFileName, out PluginRulesAssembly))
			{
				// Find all the rules source files
				Dictionary<FileReference, ModuleRulesContext> ModuleFiles = new Dictionary<FileReference, ModuleRulesContext>();
				List<FileReference> TargetFiles = new List<FileReference>();

				// Create a list of plugins for this assembly. We need to override the parent plugin, if it exists, due to overriding the
				// setting for bClassifyAsGameModuleForUHT below.
				List<PluginInfo> ForeignPlugins = new List<PluginInfo>();
				if (!Parent.EnumeratePlugins().Any(x => x.File == PluginFileName && x.Type == PluginType.Engine))
				{
					ForeignPlugins.Add(new PluginInfo(PluginFileName, PluginType.External));
				}

				// Create a new scope for the plugin. It should not reference anything else.
				RulesScope Scope = new RulesScope("Plugin", Parent.Scope);

				// Find all the modules
				ModuleRulesContext PluginModuleContext = new ModuleRulesContext(Scope, PluginFileName.Directory);
				PluginModuleContext.bClassifyAsGameModuleForUHT = !bContainsEngineModules;
				FindModuleRulesForPlugins(ForeignPlugins, PluginModuleContext, ModuleFiles);

				// Compile the assembly
				FileReference AssemblyFileName = FileReference.Combine(PluginFileName.Directory, "Intermediate", "Build", "BuildRules", Path.GetFileNameWithoutExtension(PluginFileName.FullName) + "ModuleRules" + FrameworkAssemblyExtension);
				PluginRulesAssembly = new RulesAssembly(Scope, new List<DirectoryReference> { PluginFileName.Directory }, ForeignPlugins, ModuleFiles, TargetFiles, AssemblyFileName, bContainsEngineModules, DefaultBuildSettings: null, bReadOnly: false, bSkipCompile: bSkipCompile, Parent: Parent);
				LoadedAssemblyMap.Add(PluginFileName, PluginRulesAssembly);
			}
			return PluginRulesAssembly;
		}

		/// <summary>
		/// Compile a rules assembly for the current target
		/// </summary>
		/// <param name="ProjectFile">The project file being compiled</param>
		/// <param name="TargetName">The target being built</param>
		/// <param name="bSkipRulesCompile">Whether to skip compiling any rules assemblies</param>
		/// <param name="bUsePrecompiled">Whether to use a precompiled engine/enterprise build</param>
		/// <param name="ForeignPlugin">Foreign plugin to be compiled</param>
		/// <returns>The compiled rules assembly</returns>
		public static RulesAssembly CreateTargetRulesAssembly(FileReference ProjectFile, string TargetName, bool bSkipRulesCompile, bool bUsePrecompiled, FileReference ForeignPlugin)
		{
			RulesAssembly RulesAssembly;
			if (ProjectFile != null)
			{
				RulesAssembly = CreateProjectRulesAssembly(ProjectFile, bUsePrecompiled, bSkipRulesCompile);
			}
			else
			{
				RulesAssembly = CreateEngineRulesAssembly(bUsePrecompiled, bSkipRulesCompile);

				if (RulesAssembly.GetTargetFileName(TargetName) == null && DirectoryReference.Exists(UnrealBuildTool.EnterpriseDirectory))
				{
					// Target isn't part of the engine assembly, try the enterprise assembly
					RulesAssembly = CreateEnterpriseRulesAssembly(bUsePrecompiled, bSkipRulesCompile);
				}
			}
			if (ForeignPlugin != null)
			{
				RulesAssembly = CreatePluginRulesAssembly(ForeignPlugin, bSkipRulesCompile, RulesAssembly, true);
			}
			return RulesAssembly;
		}

		/// <summary>
		/// Finds all the module rules for plugins under the given directory.
		/// </summary>
		/// <param name="Plugins">The directory to search</param>
		/// <param name="DefaultContext">The default context for any files that are enumerated</param>
		/// <param name="ModuleFileToContext">Dictionary which is filled with mappings from the module file to its corresponding context</param>
		private static void FindModuleRulesForPlugins(IReadOnlyList<PluginInfo> Plugins, ModuleRulesContext DefaultContext, Dictionary<FileReference, ModuleRulesContext> ModuleFileToContext)
		{
			PrefetchRulesFiles(Plugins.Select(x => DirectoryReference.Combine(x.Directory, "Source")));

			foreach (PluginInfo Plugin in Plugins)
			{
				List<FileReference> PluginModuleFiles = FindAllRulesFiles(DirectoryReference.Combine(Plugin.Directory, "Source"), RulesFileType.Module).ToList();
				foreach (FileReference ChildFile in Plugin.ChildFiles)
				{
					PluginModuleFiles.AddRange(FindAllRulesFiles(DirectoryReference.Combine(ChildFile.Directory, "Source"), RulesFileType.Module));
				}

				foreach (FileReference ModuleFile in PluginModuleFiles)
				{
					ModuleRulesContext PluginContext = new ModuleRulesContext(DefaultContext);
					PluginContext.DefaultOutputBaseDir = Plugin.Directory;
					PluginContext.Plugin = Plugin;
					ModuleFileToContext[ModuleFile] = PluginContext;
				}
			}
		}

		/// <summary>
		/// Gets the filename that declares the given type.
		/// </summary>
		/// <param name="ExistingType">The type to search for.</param>
		/// <returns>The filename that declared the given type, or null</returns>
		public static string GetFileNameFromType(Type ExistingType)
		{
			FileReference FileName;
			if (EngineRulesAssembly != null && EngineRulesAssembly.TryGetFileNameFromType(ExistingType, out FileName))
			{
				return FileName.FullName;
			}
			else if (EnterpriseRulesAssembly != null && EnterpriseRulesAssembly.TryGetFileNameFromType(ExistingType, out FileName))
			{
				return FileName.FullName;
			}

			foreach (RulesAssembly RulesAssembly in LoadedAssemblyMap.Values)
			{
				if (RulesAssembly.TryGetFileNameFromType(ExistingType, out FileName))
				{
					return FileName.FullName;
				}
			}
			return null;
		}
    }
}
