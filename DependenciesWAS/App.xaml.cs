﻿using Dependencies;
using Dependencies.ClrPh;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Dependencies
{
	/// <summary>
	/// Provides application-specific behavior to supplement the default Application class.
	/// </summary>
	public partial class App : Application
	{
		/// <summary>
		/// Initializes the singleton application object.  This is the first line of authored code
		/// executed, and as such is the logical equivalent of main() or WinMain().
		/// </summary>
		public App()
		{
			this.InitializeComponent();
		}


		string StatusBarMessage;

		public PE LoadBinary(string path)
		{
			StatusBarMessage = String.Format("Loading module {0:s} ...", path);

			if (!NativeFile.Exists(path))
			{
				StatusBarMessage = String.Format("Loading PE file \"{0:s}\" failed : file not present on disk.", path);
				return null;
			}

			PE pe = BinaryCache.LoadPe(path);
			if (pe == null || !pe.LoadSuccessful)
			{
				StatusBarMessage = String.Format("Loading module {0:s} failed.", path);
			}
			else
			{
				StatusBarMessage = String.Format("Loading PE file \"{0:s}\" successful.", pe.Filepath);
			}

			return pe;
		}

		/// <summary>
		/// Invoked when the application is launched normally by the end user.  Other entry points
		/// will be used such as when the application is launched to open a specific file.
		/// </summary>
		/// <param name="args">Details about the launch request and process.</param>
		protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
		{

			Phlib.InitializePhLib();

			// Load singleton for binary caching
#if TODO
			BinaryCache.InitializeBinaryCache(Dependencies.BinaryCacheOption.GetGlobalBehaviour() == Dependencies.BinaryCacheOption.BinaryCacheOptionValue.Yes);
#else
			BinaryCache.InitializeBinaryCache(false);

#endif

			mainWindow = new MainWindow();

			switch (Phlib.GetClrPhArch())
			{
				case CLRPH_ARCH.x86:
					mainWindow.Title = "Dependencies (x86)";
					break;
				case CLRPH_ARCH.x64:
					mainWindow.Title = "Dependencies (x64)";
					break;
				case CLRPH_ARCH.WOW64:
					mainWindow.Title = "Dependencies (WoW64)";
					break;
			}

			mainWindow.Activate();
		}

#if TODO
		void AppExit()
		{
            BinaryCache.Instance.Unload();
		}
#endif

		private Window mainWindow;
	}
}
