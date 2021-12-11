﻿using Dependencies;
using Dependencies.ClrPh;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Win32;
using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Popups;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Dependencies
{

	/// <summary>
	/// ImportContext : Describe an import module parsed from a PE.
	/// Only used during the dependency tree building phase
	/// </summary>
	public struct ImportContext
	{
		// Import "identifier" 
		public string ModuleName;

		// Return how the module was found (NOT_FOUND otherwise)
		public ModuleSearchStrategy ModuleLocation;

		// If found, set the filepath and parsed PE, otherwise it's null
		public string PeFilePath;
		public PE PeProperties;

		// Some imports are from api sets
		public bool IsApiSet;
		public string ApiSetModuleName;

		// module flag attributes
		public ModuleFlag Flags;
	}


    /// <summary>
    /// Dependency tree building behaviour.
    /// A full recursive dependency tree can be memory intensive, therefore the
    /// choice is left to the user to override the default behaviour.
    /// </summary>
    public class TreeBuildingBehaviour : IValueConverter
    {
        public enum DependencyTreeBehaviour
        {
            ChildOnly,
            RecursiveOnlyOnDirectImports,
            Recursive,

        }

        public static DependencyTreeBehaviour GetGlobalBehaviour()
        {
            return (DependencyTreeBehaviour)(new TreeBuildingBehaviour()).Convert(
                Dependencies.Properties.Settings.Default.TreeBuildBehaviour,
                null,// targetType
                null,// parameter
                null // System.Globalization.CultureInfo
            );
        }

        #region TreeBuildingBehaviour.IValueConverter_contract
        public object Convert(object value, Type targetType, object parameter, string culture)
        {
            string StrBehaviour = (string)value;

            switch (StrBehaviour)
            {
                default:
                case "ChildOnly":
                    return DependencyTreeBehaviour.ChildOnly;
                case "RecursiveOnlyOnDirectImports":
                    return DependencyTreeBehaviour.RecursiveOnlyOnDirectImports;
                case "Recursive":
                    return DependencyTreeBehaviour.Recursive;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string culture)
        {
            DependencyTreeBehaviour Behaviour = (DependencyTreeBehaviour)value;

            switch (Behaviour)
            {
                default:
                case DependencyTreeBehaviour.ChildOnly:
                    return "ChildOnly";
                case DependencyTreeBehaviour.RecursiveOnlyOnDirectImports:
                    return "RecursiveOnlyOnDirectImports";
                case DependencyTreeBehaviour.Recursive:
                    return "Recursive";
            }
        }
        #endregion TreeBuildingBehaviour.IValueConverter_contract
    }


    /// <summary>
    /// Dependency tree building behaviour.
    /// A full recursive dependency tree can be memory intensive, therefore the
    /// choice is left to the user to override the default behaviour.
    /// </summary>
    public class BinaryCacheOption : IValueConverter
    {
        public enum BinaryCacheOptionValue
        {
            [Description("No (faster, but locks dll until Dependencies is closed)")]
            No = 0,

            [Description("Yes (prevents file locking issues)")]
            Yes = 1
        }

        public static BinaryCacheOptionValue GetGlobalBehaviour()
        {
            return (BinaryCacheOptionValue)(new BinaryCacheOption()).Convert(
                Dependencies.Properties.Settings.Default.BinaryCacheOptionValue,
                null,// targetType
                null,// parameter
                null // System.Globalization.CultureInfo
            );
        }

        #region BinaryCacheOption.IValueConverter_contract
        public object Convert(object value, Type targetType, object parameter, string culture)
        {
            bool StrOption = (bool)value;

            switch (StrOption)
            {
                default:
                case true:
                    return BinaryCacheOptionValue.Yes;
                case false:
                    return BinaryCacheOptionValue.No;
            }

        }

        public object ConvertBack(object value, Type targetType, object parameter, string culture)
        {
            BinaryCacheOptionValue Behaviour = (BinaryCacheOptionValue)(int)value;

            switch (Behaviour)
            {
                default:
                case BinaryCacheOptionValue.Yes:
                    return true;
                case BinaryCacheOptionValue.No:
                    return false;
            }
        }
        #endregion BinaryCacheOption.IValueConverter_contract
    }

    /// <summary>
    /// User context of every dependency tree node.
    /// </summary>
    public struct DependencyNodeContext
    {
        public DependencyNodeContext(DependencyNodeContext other)
        {
            ModuleInfo = other.ModuleInfo;
            IsDummy = other.IsDummy;
        }

        /// <summary>
        /// We use a WeakReference to point towars a DisplayInfoModule
        /// in order to reduce memory allocations.
        /// </summary>
        public WeakReference ModuleInfo;

        /// <summary>
        /// Depending on the dependency tree behaviour, we may have to
        /// set up "dummy" nodes in order for the parent to display the ">" button.
        /// Those dummy are usually destroyed when their parents is expandend and imports resolved.
        /// </summary>
        public bool IsDummy;
    }

    /// <summary>
    /// Deprendency Tree custom node. It's DataContext is a DependencyNodeContext struct
    /// </summary>
    public class ModuleTreeViewItem : TreeViewNode, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public DependencyNodeContext DataContext;

        private object _Header;

        public ModuleTreeViewItem()
        {
            _importsVerified = false;
            _Parent = null;
            Dependencies.Properties.Settings.Default.PropertyChanged += this.ModuleTreeViewItem_PropertyChanged;
        }

        public ModuleTreeViewItem(ModuleTreeViewItem Parent)
        {
            _importsVerified = false;
            _Parent = Parent;
            Dependencies.Properties.Settings.Default.PropertyChanged += this.ModuleTreeViewItem_PropertyChanged;
        }

        public ModuleTreeViewItem(ModuleTreeViewItem Other, ModuleTreeViewItem Parent)
        {
            _importsVerified = false;
            _Parent = Parent;
            this.DataContext = new DependencyNodeContext((DependencyNodeContext)Other.DataContext);
            Dependencies.Properties.Settings.Default.PropertyChanged += this.ModuleTreeViewItem_PropertyChanged;
        }

        #region PropertyEventHandlers 
        public virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void ModuleTreeViewItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "FullPath")
            {
                this.Header = (object)GetTreeNodeHeaderName(Dependencies.Properties.Settings.Default.FullPath);
            }
        }
#endregion PropertyEventHandlers

#region Getters

        public string GetTreeNodeHeaderName(bool FullPath)
        {
            return (((DependencyNodeContext)this.DataContext).ModuleInfo.Target as DisplayModuleInfo).ModuleName;
        }

        public object Header
		{
            get
			{
                return _Header;
			}
            set
			{
                _Header = value;
                OnPropertyChanged(nameof(Header));
			}
		}

        public string ModuleFilePath
        {
            get
            {
                return (((DependencyNodeContext)this.DataContext).ModuleInfo.Target as DisplayModuleInfo).Filepath;
            }
        }

        public ModuleTreeViewItem ParentModule
        {
            get
            {
                return _Parent;
            }
        }


        public ModuleFlag Flags
        {
            get
            {
                return ModuleInfo.Flags;
            }
        }

        private bool _has_error;

        public bool HasErrors
        {
            get
            {
                if (!_importsVerified)
                {
                    _has_error = VerifyModuleImports();
                    _importsVerified = true;

                    // Update tooltip only once some basic checks are done
#if TODO

                    this.ToolTip = ModuleInfo.Status;
#endif
                }

                // propagate error for parent
                if (_has_error)
                {
                    ModuleTreeViewItem ParentModule = this.ParentModule;
                    if (ParentModule != null)
                    {
                        ParentModule.HasChildErrors = true;
                    }
                }

                return _has_error;
            }

            set
            {
                if (value == _has_error) return;
                _has_error = value;
                OnPropertyChanged("HasErrors");
            }
        }


        public string Tooltip
        {
            get
            {
                return ModuleInfo.Status;
            }
        }

        public bool HasChildErrors
        {
            get
            {
                return _has_child_errors;
            }
            set
            {
                if (value)
                {
                    ModuleInfo.Flags |= ModuleFlag.ChildrenError;
                }
                else
                {
                    ModuleInfo.Flags &= ~ModuleFlag.ChildrenError;
                }
#if TODO

                ToolTip = ModuleInfo.Status;
#endif
                _has_child_errors = true;
                OnPropertyChanged("HasChildErrors");

                // propagate error for parent
                ModuleTreeViewItem ParentModule = this.ParentModule;
                if (ParentModule != null)
                {
                    ParentModule.HasChildErrors = true;
                }
            }
        }

        public DisplayModuleInfo ModuleInfo
        {
            get
            {
                return (((DependencyNodeContext)this.DataContext).ModuleInfo.Target as DisplayModuleInfo);
            }
        }


        private bool VerifyModuleImports()
        {

            // current module has issues
            if ((Flags & (ModuleFlag.NotFound | ModuleFlag.MissingImports | ModuleFlag.ChildrenError)) != 0)
            {
                return true;
            }

            // no parent : it's probably the root item
            ModuleTreeViewItem ParentModule = this.ParentModule;
            if (ParentModule == null)
            {
                return false;
            }

            // Check we have any imports issues
            foreach (PeImportDll DllImport in ParentModule.ModuleInfo.Imports)
            {
                if (DllImport.Name != ModuleInfo._Name)
                    continue;



                List<Tuple<PeImport, bool>> resolvedImports = BinaryCache.LookupImports(DllImport, ModuleInfo.Filepath);
                if (resolvedImports.Count == 0)
                {
                    return true;
                }

                foreach (var Import in resolvedImports)
                {
                    if (!Import.Item2)
                    {
                        return true;
                    }
                }
            }



            return false;
        }



#endregion Getters


#region Commands 
        public RelayCommand OpenPeviewerCommand
        {
            get
            {
                if (_OpenPeviewerCommand == null)
                {
                    _OpenPeviewerCommand = new RelayCommand((param) => this.OpenPeviewer((object)param));
                }

                return _OpenPeviewerCommand;
            }
        }

        public bool OpenPeviewer(object Context)
        {
#if TODO

            string programPath = Dependencies.Properties.Settings.Default.PeViewerPath;
            Process PeviewerProcess = new Process();

            if (Context == null)
            {
                return false;
            }

            if (!File.Exists(programPath))
            {
                System.Windows.MessageBox.Show(String.Format("{0:s} file could not be found !", programPath));
                return false;
            }

            string Filepath = ModuleFilePath;
            if (Filepath == null)
            {
                return false;
            }

            PeviewerProcess.StartInfo.FileName = String.Format("\"{0:s}\"", programPath);
            PeviewerProcess.StartInfo.Arguments = String.Format("\"{0:s}\"", Filepath);
            return PeviewerProcess.Start();
#endif
            return false;
        }

        public RelayCommand OpenNewAppCommand
        {
            get
            {
                #if TODO

                if (_OpenNewAppCommand == null)
                {
                    _OpenNewAppCommand = new RelayCommand((param) =>
                    {
                        string Filepath = ModuleFilePath;
                        if (Filepath == null)
                        {
                            return;
                        }

                        Process OtherDependenciesProcess = new Process();
                        OtherDependenciesProcess.StartInfo.FileName = System.Windows.Forms.Application.ExecutablePath;
                        OtherDependenciesProcess.StartInfo.Arguments = String.Format("\"{0:s}\"", Filepath);
                        OtherDependenciesProcess.Start();
                    });
                }
#endif
                return _OpenNewAppCommand;
            }
        }

#endregion // Commands 

        private RelayCommand _OpenPeviewerCommand;
        private RelayCommand _OpenNewAppCommand;
        private ModuleTreeViewItem _Parent;
        private bool _importsVerified;
        private bool _has_child_errors;


    }


    public sealed partial class DependencyWindow : TabViewItem
	{
		PE Pe;
		public string RootFolder;
		public string WorkingDirectory;
		string Filename;
		PhSymbolProvider SymPrv;
		SxsEntries SxsEntriesCache;
		ApiSetSchema ApiSetmapCache;
		ModulesCache ProcessedModulesCache;
		DisplayModuleInfo _SelectedModule;
		bool _DisplayWarning;

		public List<string> CustomSearchFolders;

		public DependencyWindow(String Filename, List<string> CustomSearchFolders = null)
        {
			this.InitializeComponent();

            if (CustomSearchFolders != null)
            {
                this.CustomSearchFolders = CustomSearchFolders;
            }
            else
            {
                this.CustomSearchFolders = new List<string>();
            }

            this.Filename = Filename;
            this.WorkingDirectory = Path.GetDirectoryName(this.Filename);
            InitializeView();
        }

        public void InitializeView()
        {
            if (!NativeFile.Exists(this.Filename))
            {
#if TODO
                MessageBox.Show(
                    String.Format("{0:s} is not present on the disk", this.Filename),
                    "Invalid PE",
                    MessageBoxButton.OK
                );
#endif
                return;
            }

            this.Pe = (Application.Current as App).LoadBinary(this.Filename);
            if (this.Pe == null || !this.Pe.LoadSuccessful)
            {
#if TODO

                MessageBox.Show(
                    String.Format("{0:s} is not a valid PE-COFF file", this.Filename),
                    "Invalid PE",
                    MessageBoxButton.OK
                );
#endif
                return;
            }

            this.SymPrv = new PhSymbolProvider();
            this.RootFolder = Path.GetDirectoryName(this.Filename);
            this.SxsEntriesCache = SxsManifest.GetSxsEntries(this.Pe);
            this.ProcessedModulesCache = new ModulesCache();
            this.ApiSetmapCache = Phlib.GetApiSetSchema();
            this._SelectedModule = null;
            this._DisplayWarning = false;

            // TODO : Find a way to properly bind commands instead of using this hack
#if TODO
            this.ModulesList.Items.Clear();
            this.ModulesList.DoFindModuleInTreeCommand = DoFindModuleInTree;
            this.ModulesList.ConfigureSearchOrderCommand = ConfigureSearchOrderCommand;
#endif
            var RootFilename = Path.GetFileName(this.Filename);
            var RootModule = new DisplayModuleInfo(RootFilename, this.Pe, ModuleSearchStrategy.ROOT);
            this.ProcessedModulesCache.Add(new ModuleCacheKey(RootFilename, this.Filename), RootModule);

            ModuleTreeViewItem treeNode = new ModuleTreeViewItem();
            DependencyNodeContext childTreeInfoContext = new DependencyNodeContext()
            {
                ModuleInfo = new WeakReference(RootModule),
                IsDummy = false
            };

            treeNode.DataContext = childTreeInfoContext;
            treeNode.Header = treeNode.GetTreeNodeHeaderName(Dependencies.Properties.Settings.Default.FullPath);
            treeNode.IsExpanded = true;

            this.DllTreeView.RootNodes.Clear();
            this.DllTreeView.RootNodes.Add(treeNode);

            // Recursively construct tree of dll imports
            ConstructDependencyTree(treeNode, this.Pe);
        }

        #region TreeConstruction

        private ImportContext ResolveImport(PeImportDll DllImport)
        {
            ImportContext ImportModule = new ImportContext();

            ImportModule.PeFilePath = null;
            ImportModule.PeProperties = null;
            ImportModule.ModuleName = DllImport.Name;
            ImportModule.ApiSetModuleName = null;
            ImportModule.Flags = 0;
            if (DllImport.IsDelayLoad())
            {
                ImportModule.Flags |= ModuleFlag.DelayLoad;
            }

            Tuple<ModuleSearchStrategy, PE> ResolvedModule = BinaryCache.ResolveModule(
                    this.Pe,
                    DllImport.Name,
                    this.SxsEntriesCache,
                    this.CustomSearchFolders,
                    this.WorkingDirectory
                );

            ImportModule.ModuleLocation = ResolvedModule.Item1;
            if (ImportModule.ModuleLocation != ModuleSearchStrategy.NOT_FOUND)
            {
                ImportModule.PeProperties = ResolvedModule.Item2;

                if (ResolvedModule.Item2 != null)
                {
                    ImportModule.PeFilePath = ResolvedModule.Item2.Filepath;
                    foreach (var Import in BinaryCache.LookupImports(DllImport, ImportModule.PeFilePath))
                    {
                        if (!Import.Item2)
                        {
                            ImportModule.Flags |= ModuleFlag.MissingImports;
                            break;
                        }

                    }
                }
            }
            else
            {
                ImportModule.Flags |= ModuleFlag.NotFound;
            }

            // special case for apiset schema
            ImportModule.IsApiSet = (ImportModule.ModuleLocation == ModuleSearchStrategy.ApiSetSchema);
            if (ImportModule.IsApiSet)
            {
                ImportModule.Flags |= ModuleFlag.ApiSet;
                ImportModule.ApiSetModuleName = BinaryCache.LookupApiSetLibrary(DllImport.Name);

                if (DllImport.Name.StartsWith("ext-"))
                {
                    ImportModule.Flags |= ModuleFlag.ApiSetExt;
                }
            }

            return ImportModule;
        }

        private void TriggerWarningOnAppvIsvImports(string DllImportName)
        {
            if (String.Compare(DllImportName, "AppvIsvSubsystems32.dll", StringComparison.OrdinalIgnoreCase) == 0 ||
                    String.Compare(DllImportName, "AppvIsvSubsystems64.dll", StringComparison.OrdinalIgnoreCase) == 0)
            {
                if (!this._DisplayWarning)
                {
#if TODO
                    MessageBoxResult result = MessageBox.Show(
                    "This binary use the App-V containerization technology which fiddle with search directories and PATH env in ways Dependencies can't handle.\n\nFollowing results are probably not quite exact.",
                    "App-V ISV disclaimer"
                    );
#endif
                    this._DisplayWarning = true; // prevent the same warning window to popup several times
                }

            }
        }

        private void ProcessAppInitDlls(Dictionary<string, ImportContext> NewTreeContexts, PE AnalyzedPe, ImportContext ImportModule)
        {
            List<PeImportDll> PeImports = AnalyzedPe.GetImports();

            // only user32 triggers appinit dlls
            string User32Filepath = Path.Combine(FindPe.GetSystemPath(this.Pe), "user32.dll");
            if (ImportModule.PeFilePath != User32Filepath)
            {
                return;
            }

            string AppInitRegistryKey =
                (this.Pe.IsArm32Dll()) ?
                "SOFTWARE\\WowAA32Node\\Microsoft\\Windows NT\\CurrentVersion\\Windows" :
                (this.Pe.IsWow64Dll()) ?
                "SOFTWARE\\Wow6432Node\\Microsoft\\Windows NT\\CurrentVersion\\Windows" :
                "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Windows";

            // Opening registry values
            RegistryKey localKey = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry64);
            localKey = localKey.OpenSubKey(AppInitRegistryKey);
            int LoadAppInitDlls = (int)localKey.GetValue("LoadAppInit_DLLs", 0);
            string AppInitDlls = (string)localKey.GetValue("AppInit_DLLs", "");
            if (LoadAppInitDlls == 0 || String.IsNullOrEmpty(AppInitDlls))
            {
                return;
            }

            // Extremely crude parser. TODO : Add support for quotes wrapped paths with spaces
            foreach (var AppInitDll in AppInitDlls.Split(' '))
            {
                Debug.WriteLine("AppInit loading " + AppInitDll);

                // Do not process twice the same imported module
                if (null != PeImports.Find(module => module.Name == AppInitDll))
                {
                    continue;
                }

                if (NewTreeContexts.ContainsKey(AppInitDll))
                {
                    continue;
                }

                ImportContext AppInitImportModule = new ImportContext();
                AppInitImportModule.PeFilePath = null;
                AppInitImportModule.PeProperties = null;
                AppInitImportModule.ModuleName = AppInitDll;
                AppInitImportModule.ApiSetModuleName = null;
                AppInitImportModule.Flags = 0;
                AppInitImportModule.ModuleLocation = ModuleSearchStrategy.AppInitDLL;



                Tuple<ModuleSearchStrategy, PE> ResolvedAppInitModule = BinaryCache.ResolveModule(
                    this.Pe,
                    AppInitDll,
                    this.SxsEntriesCache,
                    this.CustomSearchFolders,
                    this.WorkingDirectory
                );
                if (ResolvedAppInitModule.Item1 != ModuleSearchStrategy.NOT_FOUND)
                {
                    AppInitImportModule.PeProperties = ResolvedAppInitModule.Item2;
                    AppInitImportModule.PeFilePath = ResolvedAppInitModule.Item2.Filepath;
                }
                else
                {
                    AppInitImportModule.Flags |= ModuleFlag.NotFound;
                }

                NewTreeContexts.Add(AppInitDll, AppInitImportModule);
            }
        }

        private void ProcessClrImports(Dictionary<string, ImportContext> NewTreeContexts, PE AnalyzedPe, ImportContext ImportModule)
        {
            List<PeImportDll> PeImports = AnalyzedPe.GetImports();

            // only mscorre triggers clr parsing
            string User32Filepath = Path.Combine(FindPe.GetSystemPath(this.Pe), "mscoree.dll");
            if (ImportModule.PeFilePath != User32Filepath)
            {
                return;
            }

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(RootFolder);

            // Parse it via cecil
            AssemblyDefinition PeAssembly = null;
            try
            {
                PeAssembly = AssemblyDefinition.ReadAssembly(AnalyzedPe.Filepath);
            }
            catch (BadImageFormatException)
            {
#if TODO
                MessageBoxResult result = MessageBox.Show(
                        String.Format("Cecil could not correctly parse {0:s}, which can happens on .NET Core executables. CLR imports will be not shown", AnalyzedPe.Filepath),
                        "CLR parsing fail"
                );
#endif
                return;
            }

            foreach (var module in PeAssembly.Modules)
            {
                // Process CLR referenced assemblies
                foreach (var assembly in module.AssemblyReferences)
                {
                    AssemblyDefinition definition;
                    try
                    {
                        definition = resolver.Resolve(assembly);
                    }
                    catch (AssemblyResolutionException)
                    {
                        ImportContext AppInitImportModule = new ImportContext();
                        AppInitImportModule.PeFilePath = null;
                        AppInitImportModule.PeProperties = null;
                        AppInitImportModule.ModuleName = Path.GetFileName(assembly.Name);
                        AppInitImportModule.ApiSetModuleName = null;
                        AppInitImportModule.Flags = ModuleFlag.ClrReference;
                        AppInitImportModule.ModuleLocation = ModuleSearchStrategy.ClrAssembly;
                        AppInitImportModule.Flags |= ModuleFlag.NotFound;

                        if (!NewTreeContexts.ContainsKey(AppInitImportModule.ModuleName))
                        {
                            NewTreeContexts.Add(AppInitImportModule.ModuleName, AppInitImportModule);
                        }

                        continue;
                    }

                    foreach (var AssemblyModule in definition.Modules)
                    {
                        Debug.WriteLine("Referenced Assembling loading " + AssemblyModule.Name + " : " + AssemblyModule.FileName);

                        // Do not process twice the same imported module
                        if (null != PeImports.Find(mod => mod.Name == Path.GetFileName(AssemblyModule.FileName)))
                        {
                            continue;
                        }

                        ImportContext AppInitImportModule = new ImportContext();
                        AppInitImportModule.PeFilePath = null;
                        AppInitImportModule.PeProperties = null;
                        AppInitImportModule.ModuleName = Path.GetFileName(AssemblyModule.FileName);
                        AppInitImportModule.ApiSetModuleName = null;
                        AppInitImportModule.Flags = ModuleFlag.ClrReference;
                        AppInitImportModule.ModuleLocation = ModuleSearchStrategy.ClrAssembly;

                        Tuple<ModuleSearchStrategy, PE> ResolvedAppInitModule = BinaryCache.ResolveModule(
                            this.Pe,
                            AssemblyModule.FileName,
                            this.SxsEntriesCache,
                            this.CustomSearchFolders,
                            this.WorkingDirectory
                        );
                        if (ResolvedAppInitModule.Item1 != ModuleSearchStrategy.NOT_FOUND)
                        {
                            AppInitImportModule.PeProperties = ResolvedAppInitModule.Item2;
                            AppInitImportModule.PeFilePath = ResolvedAppInitModule.Item2.Filepath;
                        }
                        else
                        {
                            AppInitImportModule.Flags |= ModuleFlag.NotFound;
                        }

                        if (!NewTreeContexts.ContainsKey(AppInitImportModule.ModuleName))
                        {
                            NewTreeContexts.Add(AppInitImportModule.ModuleName, AppInitImportModule);
                        }
                    }

                }

                // Process unmanaged dlls for native calls
                foreach (var UnmanagedModule in module.ModuleReferences)
                {
                    // some clr dll have a reference to an "empty" dll
                    if (UnmanagedModule.Name.Length == 0)
                    {
                        continue;
                    }

                    Debug.WriteLine("Referenced module loading " + UnmanagedModule.Name);

                    // Do not process twice the same imported module
                    if (null != PeImports.Find(m => m.Name == UnmanagedModule.Name))
                    {
                        continue;
                    }



                    ImportContext AppInitImportModule = new ImportContext();
                    AppInitImportModule.PeFilePath = null;
                    AppInitImportModule.PeProperties = null;
                    AppInitImportModule.ModuleName = UnmanagedModule.Name;
                    AppInitImportModule.ApiSetModuleName = null;
                    AppInitImportModule.Flags = ModuleFlag.ClrReference;
                    AppInitImportModule.ModuleLocation = ModuleSearchStrategy.ClrAssembly;

                    Tuple<ModuleSearchStrategy, PE> ResolvedAppInitModule = BinaryCache.ResolveModule(
                        this.Pe,
                        UnmanagedModule.Name,
                        this.SxsEntriesCache,
                        this.CustomSearchFolders,
                        this.WorkingDirectory
                    );
                    if (ResolvedAppInitModule.Item1 != ModuleSearchStrategy.NOT_FOUND)
                    {
                        AppInitImportModule.PeProperties = ResolvedAppInitModule.Item2;
                        AppInitImportModule.PeFilePath = ResolvedAppInitModule.Item2.Filepath;
                    }

                    if (!NewTreeContexts.ContainsKey(AppInitImportModule.ModuleName))
                    {
                        NewTreeContexts.Add(AppInitImportModule.ModuleName, AppInitImportModule);
                    }
                }
            }
        }

        /// <summary>
        /// Background processing of a single PE file.
        /// It can be lengthy since there are disk access (and misses).
        /// </summary>
        /// <param name="NewTreeContexts"> This variable is passed as reference to be updated since this function is run in a separate thread. </param>
        /// <param name="newPe"> Current PE file analyzed </param>
        private void ProcessPe(Dictionary<string, ImportContext> NewTreeContexts, PE newPe)
        {
            List<PeImportDll> PeImports = newPe.GetImports();

            foreach (PeImportDll DllImport in PeImports)
            {
                // Ignore already processed imports
                if (NewTreeContexts.ContainsKey(DllImport.Name))
                {
                    continue;
                }

                // Find Dll in "paths"
                ImportContext ImportModule = ResolveImport(DllImport);

                // add warning for appv isv applications 
                TriggerWarningOnAppvIsvImports(DllImport.Name);


                NewTreeContexts.Add(DllImport.Name, ImportModule);


                // AppInitDlls are triggered by user32.dll, so if the binary does not import user32.dll they are not loaded.
                ProcessAppInitDlls(NewTreeContexts, newPe, ImportModule);


                // if mscoree.dll is imported, it means the module is a C# assembly, and we can use Mono.Cecil to enumerate its references
                ProcessClrImports(NewTreeContexts, newPe, ImportModule);
            }
        }

        private class BacklogImport : Tuple<ModuleTreeViewItem, string>
        {
            public BacklogImport(ModuleTreeViewItem Node, string Filepath)
            : base(Node, Filepath)
            {
            }
        }

        private void ConstructDependencyTree(ModuleTreeViewItem RootNode, string FilePath, int RecursionLevel = 0)
        {
            PE CurrentPE = (Application.Current as App).LoadBinary(FilePath);

            if (null == CurrentPE)
            {
                return;
            }

            ConstructDependencyTree(RootNode, CurrentPE, RecursionLevel);
        }

        private void ConstructDependencyTree(ModuleTreeViewItem RootNode, PE CurrentPE, int RecursionLevel = 0)
        {
            // "Closured" variables (it 's a scope hack really).
            Dictionary<string, ImportContext> NewTreeContexts = new Dictionary<string, ImportContext>();

            BackgroundWorker bw = new BackgroundWorker();
            bw.WorkerReportsProgress = true; // useless here for now


            bw.DoWork += (sender, e) => {

                ProcessPe(NewTreeContexts, CurrentPE);
            };


            bw.RunWorkerCompleted += (sender, e) =>
            {
                TreeBuildingBehaviour.DependencyTreeBehaviour SettingTreeBehaviour = Dependencies.TreeBuildingBehaviour.GetGlobalBehaviour();
                List<ModuleTreeViewItem> PeWithDummyEntries = new List<ModuleTreeViewItem>();
                List<BacklogImport> PEProcessingBacklog = new List<BacklogImport>();

                // Important !
                // 
                // This handler is executed in the STA (Single Thread Application)
                // which is authorized to manipulate UI elements. The BackgroundWorker is not.
                //

                foreach (ImportContext NewTreeContext in NewTreeContexts.Values)
                {
                    ModuleTreeViewItem childTreeNode = new ModuleTreeViewItem(RootNode);
                    DependencyNodeContext childTreeNodeContext = new DependencyNodeContext();
                    childTreeNodeContext.IsDummy = false;

                    string ModuleName = NewTreeContext.ModuleName;
                    string ModuleFilePath = NewTreeContext.PeFilePath;
                    ModuleCacheKey ModuleKey = new ModuleCacheKey(NewTreeContext);

                    // Newly seen modules
                    if (!this.ProcessedModulesCache.ContainsKey(ModuleKey))
                    {
                        // Missing module "found"
                        if ((NewTreeContext.PeFilePath == null) || !NativeFile.Exists(NewTreeContext.PeFilePath))
                        {
                            if (NewTreeContext.IsApiSet)
                            {
                                this.ProcessedModulesCache[ModuleKey] = new ApiSetNotFoundModuleInfo(ModuleName, NewTreeContext.ApiSetModuleName);
                            }
                            else
                            {
                                this.ProcessedModulesCache[ModuleKey] = new NotFoundModuleInfo(ModuleName);
                            }

                        }
                        else
                        {


                            if (NewTreeContext.IsApiSet)
                            {
                                var ApiSetContractModule = new DisplayModuleInfo(NewTreeContext.ApiSetModuleName, NewTreeContext.PeProperties, NewTreeContext.ModuleLocation, NewTreeContext.Flags);
                                var NewModule = new ApiSetModuleInfo(NewTreeContext.ModuleName, ref ApiSetContractModule);

                                this.ProcessedModulesCache[ModuleKey] = NewModule;

                                if (SettingTreeBehaviour == TreeBuildingBehaviour.DependencyTreeBehaviour.Recursive)
                                {
                                    PEProcessingBacklog.Add(new BacklogImport(childTreeNode, ApiSetContractModule.ModuleName));
                                }
                            }
                            else
                            {
                                var NewModule = new DisplayModuleInfo(NewTreeContext.ModuleName, NewTreeContext.PeProperties, NewTreeContext.ModuleLocation, NewTreeContext.Flags);
                                this.ProcessedModulesCache[ModuleKey] = NewModule;

                                switch (SettingTreeBehaviour)
                                {
                                    case TreeBuildingBehaviour.DependencyTreeBehaviour.RecursiveOnlyOnDirectImports:
                                        if ((NewTreeContext.Flags & ModuleFlag.DelayLoad) == 0)
                                        {
                                            PEProcessingBacklog.Add(new BacklogImport(childTreeNode, NewModule.ModuleName));
                                        }
                                        break;

                                    case TreeBuildingBehaviour.DependencyTreeBehaviour.Recursive:
                                        PEProcessingBacklog.Add(new BacklogImport(childTreeNode, NewModule.ModuleName));
                                        break;
                                }
                            }
                        }

                        // add it to the module list
#if TODO
                        this.ModulesList.AddModule(this.ProcessedModulesCache[ModuleKey]);
#endif
                    }

                    // Since we uniquely process PE, for thoses who have already been "seen",
                    // we set a dummy entry in order to set the "[+]" icon next to the node.
                    // The dll dependencies are actually resolved on user double-click action
                    // We can't do the resolution in the same time as the tree construction since
                    // it's asynchronous (we would have to wait for all the background to finish and
                    // use another Async worker to resolve).

                    if ((NewTreeContext.PeProperties != null) && (NewTreeContext.PeProperties.GetImports().Count > 0))
                    {
#if TODO
  ModuleTreeViewItem DummyEntry = new ModuleTreeViewItem();
                        DependencyNodeContext DummyContext = new DependencyNodeContext()
                        {
                            ModuleInfo = new WeakReference(new NotFoundModuleInfo("Dummy")),
                            IsDummy = true
                        };

                        DummyEntry.DataContext = DummyContext;
                        DummyEntry.Header = "@Dummy : if you see this header, it's a bug.";
                        DummyEntry.IsExpanded = false;

#else
                        childTreeNode.HasUnrealizedChildren = true;

#endif

#if TODO
                        childTreeNode.Expanded += ResolveDummyEntries;
#endif
                    }

                    // Add to tree view
                    childTreeNodeContext.ModuleInfo = new WeakReference(this.ProcessedModulesCache[ModuleKey]);
                    childTreeNode.DataContext = childTreeNodeContext;
                    childTreeNode.Header = childTreeNode.GetTreeNodeHeaderName(Dependencies.Properties.Settings.Default.FullPath);
#if TODO
#else
                    bool hasErrors = childTreeNode.HasErrors; // Needs to be called to propagate errors to the parent node. Might not be called due to virtualization
#endif
                    RootNode.Children.Add(childTreeNode);
                }


                // Process next batch of dll imports only if :
                //	1. Recursive tree building has been activated
                //  2. Recursion is not hitting the max depth level
                bool doProcessNextLevel = (SettingTreeBehaviour != TreeBuildingBehaviour.DependencyTreeBehaviour.ChildOnly) &&
                                          (RecursionLevel < Dependencies.Properties.Settings.Default.TreeDepth);

                if (doProcessNextLevel)
                {
                    foreach (var ImportNode in PEProcessingBacklog)
                    {
                        ConstructDependencyTree(ImportNode.Item1, ImportNode.Item2, RecursionLevel + 1); // warning : recursive call
                    }
                }


            };

            bw.RunWorkerAsync();
        }

        /// <summary>
        /// Resolve imports when the user expand the node.
        /// </summary>
        private void DllTreeView_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
		{
            ModuleTreeViewItem NeedDummyPeNode = args.Node as ModuleTreeViewItem;

            if (NeedDummyPeNode.HasUnrealizedChildren == false)
            {
                return;
            }
            string Filepath = NeedDummyPeNode.ModuleFilePath;

            NeedDummyPeNode.HasUnrealizedChildren = false;

            ConstructDependencyTree(NeedDummyPeNode, Filepath);
        }
	}
}
#endregion TreeConstruction

