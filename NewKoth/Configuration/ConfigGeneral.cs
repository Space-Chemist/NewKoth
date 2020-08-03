using System.Xml.Serialization;
using Sandbox.Common;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;
using KingOfTheHill;
using KingOfTheHill.Configuration;
using System;
using System.IO;
using KingOfTheHill.Template;

namespace KingOfTheHill.Configuration
{
	[XmlRoot("Prize")]
	public class ConfigGeneral
	{
		
			
		
		
		public string[] PrizeComponentSubtypeId { get; set; }
		
		public string[] PrizeOreSubtypeId { get; set; }
		
		public string[] PrizeIngotSubtypeId { get; set; }

		public ConfigGeneral()
		{			
						
			
			
			PrizeComponentSubtypeId = new string[] { "Prize_Component_SubtypeId_here", };
			
			PrizeOreSubtypeId = new string[] { "Prize_Ore_SubtypeId_here", };
			
			PrizeIngotSubtypeId = new string[] { "Prize_Ignot_SubtypeId_here" };


		}

		public ConfigGeneral LoadSettings()
		{
			if (MyAPIGateway.Utilities.FileExistsInLocalStorage("PrizeSettings.xml", typeof(ConfigGeneral)) == true)
			{
				try
				{
					ConfigGeneral config = null;
					var reader = MyAPIGateway.Utilities.ReadFileInLocalStorage("PrizeSettings.xml", typeof(ConfigGeneral));
					string configcontents = reader.ReadToEnd();
					config = MyAPIGateway.Utilities.SerializeFromXML<ConfigGeneral>(configcontents);
					//MyVisualScriptLogicProvider.SendChatMessage(config.ToString(), "config: ");


					return config;
				}
				catch (Exception)
				{

					var defaultSettings = new ConfigGeneral();

					return defaultSettings;
				}
			}

			var settings = new ConfigGeneral();
			try
			{
				//MyVisualScriptLogicProvider.SendChatMessage("new config.", "Debug");
				using (var writer = MyAPIGateway.Utilities.WriteFileInLocalStorage("PrizeSettings.xml", typeof(ConfigGeneral)))
				{
					writer.Write(MyAPIGateway.Utilities.SerializeToXML(settings));
					//writer.Write("config");
					//MyVisualScriptLogicProvider.SendChatMessage(settings.ToString(), "Settings: ");
					//MyVisualScriptLogicProvider.SendChatMessage("new config made.", "Debug");

				}

			}
			catch (Exception)
			{

			}

			return settings;
		}

		public string SaveSettings(ConfigGeneral settings)
		{
			try
			{
				using (var writer = MyAPIGateway.Utilities.WriteFileInLocalStorage("PrizeSettings.xml", typeof(ConfigGeneral)))
				{

					writer.Write(MyAPIGateway.Utilities.SerializeToXML(settings));

				}



				return "Settings Updated Successfully.";
			}
			catch (Exception)
			{

			}

			return "Settings Changed, But Could Not Be Saved To XML. Changes May Be Lost On Session Reload.";
		}
	}
}
