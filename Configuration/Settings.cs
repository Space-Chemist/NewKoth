using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KingOfTheHill.Configuration
{
    class Settings
    {
		public static ConfigGeneral General = new ConfigGeneral();

		public static void LoadSettings()
		{
			General = General.LoadSettings();
		}
	}
}

