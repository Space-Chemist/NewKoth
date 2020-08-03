using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.Utils;
using KingOfTheHill.Descriptions;
using ModNetworkAPI;
using VRage.ObjectBuilders;
using Sandbox.Game;
using Sandbox.Game.Entities;
using KingOfTheHill.Configuration;
using KingOfTheHill.Template;
using KingOfTheHill;
using System.Runtime.CompilerServices;
using System.Security.Policy;

namespace KingOfTheHill
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class Core : MySessionComponentBase
    {        
        public const string Keyword = "/koth";
        public const string DisplayName = "KotH";
        public const ushort ComId = 42511;
        public string g;
        private static List<ZoneBlock> Zones = new List<ZoneBlock>();

        private static List<WorldDescription> Worlds = new List<WorldDescription>();

        private bool IsInitilaized = false;
        private int interval = 0;        

        private NetworkAPI Network => NetworkAPI.Instance;

        

        public static void RegisterZone(ZoneBlock zone)
        {
            Zones.Add(zone);
        }

        public static void UnRegisterZone(ZoneBlock zone)
        {
            Zones.Remove(zone);
        }

        public override void Init(MyObjectBuilder_SessionComponent sessionComponent)
        {
            Tools.Log(MyLogSeverity.Info, "Initializing");
            
            Settings.LoadSettings();
            LoadData();
            if (!NetworkAPI.IsInitialized)
            {
                NetworkAPI.Init(ComId, DisplayName, Keyword);                
            }

            Network.RegisterChatCommand(string.Empty, Chat_Help);
            Network.RegisterChatCommand("help", Chat_Help);
            //MyVisualScriptLogicProvider.SendChatMessage(Network.NetworkType.ToString());
            if (Network.NetworkType == NetworkTypes.Client)
            {
                //MyVisualScriptLogicProvider.SendChatMessage("does not know it is a server");
                Network.RegisterChatCommand("Clear-Score", (args) => Network.SendCommand("Clear"));
                Network.RegisterChatCommand("score", (args) => Network.SendCommand("score"));
                Network.RegisterChatCommand("save", (args) => Network.SendCommand("save"));
                Network.RegisterChatCommand("force-load", (args) => Network.SendCommand("force-load"));

                Network.RegisterNetworkCommand("update", ClientCallback_Update);
                Network.RegisterNetworkCommand("score", ClientCallback_Score);
                Network.RegisterNetworkCommand("sync_zone", ClientCallback_SyncZone);
                Network.RegisterNetworkCommand("Clear", ServerCallback_Clear);
            }
            else
            {
                //MyVisualScriptLogicProvider.SendChatMessage("knows it is a server");
                IsInitilaized = true;
                ZoneBlock.OnAwardPoints += AwardPointsandRewards;
                ZoneBlock.OnPlayerDied += PlayerDied;

                Network.RegisterChatCommand("score",
                    (args) =>
                    {
                        MyAPIGateway.Utilities.ShowMissionScreen(Network.ModName, "King of the Hill", "",
                            FormatScores());
                    });
                Network.RegisterChatCommand("save",
                    (args) => { ServerCallback_Save(MyAPIGateway.Session.Player.SteamUserId, "save", null); });
                Network.RegisterChatCommand("Clear-Score",
                   (args) => { ServerCallback_Clear(MyAPIGateway.Session.Player.SteamUserId, "Clear", null); });
                Network.RegisterChatCommand("force-load",
                    (args) =>
                    {
                        ServerCallback_ForceLoad(MyAPIGateway.Session.Player.SteamUserId, "force_load", null);
                    });

                Network.RegisterNetworkCommand("update", ServerCallback_Update);
                Network.RegisterNetworkCommand("sync_zone", ServerCallback_SyncZone);
                Network.RegisterNetworkCommand("score", ServerCallback_Score);
                Network.RegisterNetworkCommand("save", ServerCallback_Save);
                Network.RegisterNetworkCommand("Clear", ServerCallback_Clear);
                Network.RegisterNetworkCommand("force_load", ServerCallback_ForceLoad);
            }


            ZoneBlock.OnUpdate += ZoneUpdate;
        }

        public override void LoadData()
        {
            Tools.Log(MyLogSeverity.Info, "Loading data");
            if (!MyAPIGateway.Multiplayer.IsServer) return;
            Session session = Descriptions.Session.Load();
            //Tools.Log(MyLogSeverity.Info, session.WorldScores.ToString());
            Worlds = session.WorldScores;
            //foreach (var world in session.WorldScores)
            //{
            //    Tools.Log(MyLogSeverity.Info, $"{world.Name}\n{world.Scores.First().Points.ToString()}");
            //}
        }

        public override void SaveData()
        {
            if (!MyAPIGateway.Multiplayer.IsServer) return;
            Session session = new Session();
            session.WorldScores = Worlds;
            Descriptions.Session.Save(session);
            foreach (ZoneBlock b in Zones)
            {
                b.Data.Save(b.Entity);
            }
        }

        private void Clearscore()
        {
            if (!MyAPIGateway.Multiplayer.IsServer) return;
            Session session = new Session();
            Worlds = session.WorldScores;
            Descriptions.Session.Save(session);
            foreach (ZoneBlock b in Zones)
            {
                b.Data.Save(b.Entity);
            }


        }


        public override void UpdateAfterSimulation()
        {
            //MyVisualScriptLogicProvider.SendChatMessage("before initilazi");
            if (!IsInitilaized)
            {
                //MyVisualScriptLogicProvider.SendChatMessage("after initilazi");
                if (interval == 300)
                {
                    //MyVisualScriptLogicProvider.SendChatMessage("interval 300");
                    Network.SendCommand("update");
                    IsInitilaized = true;
                    //MyVisualScriptLogicProvider.SendChatMessage("update called");
                }

                interval++;
            }
        }

        protected override void UnloadData()
        {
            Network.Close();
            ZoneBlock.OnUpdate -= ZoneUpdate;
            ZoneBlock.OnAwardPoints -= AwardPointsandRewards;
            ZoneBlock.OnPlayerDied -= PlayerDied;
        }

        private void AwardPointsandRewards(ZoneBlock zone, IMyFaction faction, int enemies, long gridEntityId,
            ZoneDescription zoneDescription)
        {
            Tools.Log(MyLogSeverity.Info, "points");
            
            if (MyAPIGateway.Multiplayer.IsServer)
            {
                IMyCubeGrid grid = MyAPIGateway.Entities.GetEntityById(gridEntityId) as IMyCubeGrid;
                var gts = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid);
                var blockList = new List<IMyTerminalBlock>();
                gts.GetBlocksOfType<IMyTerminalBlock>(blockList);
                //MyVisualScriptLogicProvider.SendChatMessage("AwardPointsandReward is server");
                long facId = faction.FactionId;
                var planet = MyGamePruningStructure.GetClosestPlanet(zone.Entity.GetPosition());
                string name = (planet == null) ? "N/A" : planet.Name;
                if (!Worlds.Any(description => description.Name == name))
                {
                    var world = new WorldDescription()
                    {
                        Name = name,
                        Scores = new List<ScoreDescription>()
                    };
                    Worlds.Add(world);
                }

                MyCubeGrid mgrid = MyAPIGateway.Entities.GetEntityById(gridEntityId) as MyCubeGrid;
                var mgts = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid);
                int points = 1;
                var ww = Worlds.Find(w => w.Name == name);
                var sd = ww.Scores;
                var h = sd.Any(s => s.FactionId == facId);
                string g = mgrid.DisplayName;
                //MyVisualScriptLogicProvider.SendChatMessage(g.ToString());
                if (!h)
                {
                    ww.Scores.Add(new ScoreDescription()
                    {
                        FactionId = facId,
                        FactionName = faction.Name,
                        FactionTag = faction.Tag,
                        Points = 1,
                        PlanetId = name,
                        Gridname = g,
                    });
                }
                else
                {
                    ww.Scores.Find(s => s.FactionId == facId).Points += points;
                }

                int currentPoints = ww.Scores.Find(s => s.FactionId == facId).Points;                 
                int Money = Convert.ToInt32(zone.Data.SpaceCreditRewardAmount);
                string message = "";

                foreach (var block in blockList)
                {
                    //MyVisualScriptLogicProvider.SendChatMessage("blocks blocklist");
                    if (block != null && block is IMyCargoContainer && block.BlockDefinition.SubtypeId == "Prizebox")
                    {                        
                        int PointP = Convert.ToInt32(zone.Data.PointsForPrize);                

                        //MyVisualScriptLogicProvider.SendChatMessage("prizebox found");
                        if (currentPoints % PointP == 0)
                        {
                            //MyVisualScriptLogicProvider.SendChatMessage("int to float didnt break it!");
                            if (zone.Data.UseComponentReward == true)
                            {
                                if (!zone.Data.AdvancedComponentSelection)
                                {
                                    string PrizeCL = zone.Data.ComponentListBoxOutputString;
                                    int Amounts = Convert.ToInt32(zone.Data.PrizeAmount);
                                    //MyVisualScriptLogicProvider.SendChatMessage(zone.Data.ComponentListBoxOutputString);
                                    var definitionId = new MyDefinitionId(typeof(MyObjectBuilder_Component), PrizeCL);
                                    var content = (MyObjectBuilder_Component)MyObjectBuilderSerializer.CreateNewObject(definitionId);
                                    MyObjectBuilder_InventoryItem inventoryItem = new MyObjectBuilder_InventoryItem
                                        { Amount = Amounts, Content = content };
                                    //MyVisualScriptLogicProvider.SendChatMessage("Points is greater than or equal to pointsforprize - Checking if can add items");

                                    if (block.GetInventory().CanItemsBeAdded(Amounts, definitionId) == true)
                                    {
                                        //MyVisualScriptLogicProvider.SendChatMessage("Items were added");
                                        block.GetInventory().AddItems(Amounts, inventoryItem.Content);
                                    }
                                }
                                else
                                {
                                    string PrizeCS = zone.Data.PrizeComponentSubtypeId;
                                    int Amounts = Convert.ToInt32(zone.Data.PrizeAmount);
                                    //MyVisualScriptLogicProvider.SendChatMessage(currentPoints.ToString());
                                    var definitionId = new MyDefinitionId(typeof(MyObjectBuilder_Component), PrizeCS);
                                    var content = (MyObjectBuilder_Component)MyObjectBuilderSerializer.CreateNewObject(definitionId);
                                    MyObjectBuilder_InventoryItem inventoryItem = new MyObjectBuilder_InventoryItem
                                        { Amount = Amounts, Content = content };
                                    //MyVisualScriptLogicProvider.SendChatMessage("Points is greater than or equal to pointsforprize - Checking if can add items");

                                    if (block.GetInventory().CanItemsBeAdded(Amounts, definitionId) == true)
                                    {
                                        //MyVisualScriptLogicProvider.SendChatMessage("Items were added");
                                        block.GetInventory().AddItems(Amounts, inventoryItem.Content);
                                    }
                                }
                                
                            }

                            if (zone.Data.UseOreReward == true)
                            {
                                if (!zone.Data.AdvancedOreSelection)
                                {
                                    string PrizeOL = zone.Data.OreListBoxOutputString;
                                    int AmountsO = Convert.ToInt32(zone.Data.PrizeAmountOre);
                                    //MyVisualScriptLogicProvider.SendChatMessage(currentPoints.ToString());
                                    var definitionId = new MyDefinitionId(typeof(MyObjectBuilder_Ore), PrizeOL);
                                    var content = (MyObjectBuilder_Ore)MyObjectBuilderSerializer.CreateNewObject(definitionId);
                                    MyObjectBuilder_InventoryItem inventoryItem = new MyObjectBuilder_InventoryItem
                                        { Amount = AmountsO, Content = content };
                                    //MyVisualScriptLogicProvider.SendChatMessage("Points is greater than or equal to 5 - Checking if can add items");

                                    if (block.GetInventory().CanItemsBeAdded(AmountsO, definitionId) == true)
                                    {
                                        //MyVisualScriptLogicProvider.SendChatMessage("Items were added");
                                        block.GetInventory().AddItems(AmountsO, inventoryItem.Content);
                                    }
                                }
                                else
                                {
                                    string PrizeOS = zone.Data.PrizeOreSubtypeId;
                                    int AmountsO = Convert.ToInt32(zone.Data.PrizeAmountOre);
                                    //MyVisualScriptLogicProvider.SendChatMessage(currentPoints.ToString());
                                    var definitionId = new MyDefinitionId(typeof(MyObjectBuilder_Ore), PrizeOS);
                                    var content = (MyObjectBuilder_Ore)MyObjectBuilderSerializer.CreateNewObject(definitionId);
                                    MyObjectBuilder_InventoryItem inventoryItem = new MyObjectBuilder_InventoryItem
                                        { Amount = AmountsO, Content = content };
                                    //MyVisualScriptLogicProvider.SendChatMessage("Points is greater than or equal to 5 - Checking if can add items");

                                    if (block.GetInventory().CanItemsBeAdded(AmountsO, definitionId) == true)
                                    {
                                        //MyVisualScriptLogicProvider.SendChatMessage("Items were added");
                                        block.GetInventory().AddItems(AmountsO, inventoryItem.Content);
                                    }
                                }
                                
                            }

                            if (zone.Data.UseIngotReward == true)
                            {
                                if (!zone.Data.AdvancedIngotSelection)
                                {
                                    string PrizeIL = zone.Data.IngotListBoxOutputString;
                                    int AmountsI = Convert.ToInt32(zone.Data.PrizeAmountIngot);
                                    //MyVisualScriptLogicProvider.SendChatMessage(currentPoints.ToString());
                                    var definitionId = new MyDefinitionId(typeof(MyObjectBuilder_Ingot), PrizeIL);
                                    var content = (MyObjectBuilder_Ingot)MyObjectBuilderSerializer.CreateNewObject(definitionId);
                                    MyObjectBuilder_InventoryItem inventoryItem = new MyObjectBuilder_InventoryItem
                                        { Amount = AmountsI, Content = content };
                                    //MyVisualScriptLogicProvider.SendChatMessage("Points is greater than or equal to 5 - Checking if can add items");

                                    if (block.GetInventory().CanItemsBeAdded(AmountsI, definitionId) == true)
                                    {
                                        //MyVisualScriptLogicProvider.SendChatMessage("Items were added");
                                        block.GetInventory().AddItems(AmountsI, inventoryItem.Content);
                                    }
                                }
                                else
                                {
                                    string PrizeIS = zone.Data.PrizeIngotSubtypeId;
                                    int AmountsI = Convert.ToInt32(zone.Data.PrizeAmountIngot);
                                    //MyVisualScriptLogicProvider.SendChatMessage(currentPoints.ToString());
                                    var definitionId = new MyDefinitionId(typeof(MyObjectBuilder_Ingot), PrizeIS);
                                    var content = (MyObjectBuilder_Ingot)MyObjectBuilderSerializer.CreateNewObject(definitionId);
                                    MyObjectBuilder_InventoryItem inventoryItem = new MyObjectBuilder_InventoryItem
                                        { Amount = AmountsI, Content = content };
                                    //MyVisualScriptLogicProvider.SendChatMessage("Points is greater than or equal to 5 - Checking if can add items");

                                    if (block.GetInventory().CanItemsBeAdded(AmountsI, definitionId) == true)
                                    {
                                        //MyVisualScriptLogicProvider.SendChatMessage("Items were added");
                                        block.GetInventory().AddItems(AmountsI, inventoryItem.Content);
                                    }
                                }
                                
                            }
                            //else
                            //{
                            //    MyVisualScriptLogicProvider.SendChatMessage("Items were not added");
                            //}
                            //MyVisualScriptLogicProvider.SendChatMessage("Reward process complete");
                        }
                    }
                }

                if (zone.Data.AwardPointsAsCredits)
                {
                    faction.RequestChangeBalance(points * Money);
                    message = $"{faction.Name} Scored 1 Points! ({points * Money} credits)";
                }
                else
                {
                    message = $"{faction.Name} Scored 1 Points!";
                }


                SaveData();
                MyAPIGateway.Utilities.SendModMessage(Tools.ModMessageId, $"KotH: {message}");
                Network.SendCommand("update", message: message,
                    data: MyAPIGateway.Utilities.SerializeToBinary(GenerateUpdate()));
            }
        }

        private void PlayerDied(ZoneBlock zone, IMyPlayer player, IMyFaction faction)
        {
            if (zone.Data.PointsRemovedOnDeath == 0) return;

            if (MyAPIGateway.Multiplayer.IsServer)
            {                
                var planet = MyGamePruningStructure.GetClosestPlanet(zone.Entity.GetPosition());

                string name = (planet == null) ? "N/A" : planet.Name;
                long facId = faction.FactionId;
                if (!Worlds.Any(description => description.Name == name))
                {
                    var world = new WorldDescription()
                    {
                        Name = name,
                        Scores = new List<ScoreDescription>()
                    };
                    Worlds.Add(world);
                }

                var ww = Worlds.Find(w => w.Name == name);
                var sd = ww.Scores;
                var h = sd.Any(s => s.FactionId == facId);             

                if (!h)
                {
                    ww.Scores.Add(new ScoreDescription()
                    {
                        FactionId = facId,
                        FactionName = faction.Name,
                        FactionTag = faction.Tag,
                        Points = 1,
                        PlanetId = name,
                        Gridname = g,
                    });
                }

                ww.Scores[(int)facId].Points -= zone.Data.PointsRemovedOnDeath;

                if (ww.Scores[(int)facId].Points < 1)
                {
                    ww.Scores[(int)facId].Points = 1;
                }

                string message = $"[{faction.Tag}] {player.DisplayName} Died: -1 Points";

                MyAPIGateway.Utilities.SendModMessage(Tools.ModMessageId, $"KotH: {message}");
                Network.SendCommand("update", message: message, data: MyAPIGateway.Utilities.SerializeToBinary(GenerateUpdate()));
            }
        }

        private void ZoneUpdate(ZoneBlock zone)
        {
            RequestServerUpdate();
            SaveData();
            Network.SendCommand("sync_zone", data: MyAPIGateway.Utilities.SerializeToBinary(zone.Data));
        }

        private Update GenerateUpdate()
        {
            Update value = new Update();

            foreach (ZoneBlock block in Zones)
            {
                value.Zones.Add(block.Data);
            }

            return value;
        }


        private string FormatScores()
        {
            StringBuilder formatedScores = new StringBuilder();
            foreach (var world in Worlds)
            {
                foreach (ScoreDescription score in world.Scores)
                {
                    formatedScores.AppendLine(
                        value: $"[{score.FactionTag}] {score.FactionName}: {score.Points.ToString()}");
                }
            }

            return formatedScores.ToString();
        }

        private void RequestServerUpdate()
        {
            if (Network.NetworkType == NetworkTypes.Client)
            {
                Network.SendCommand("update");
            }
        }

        #region Network Communication

        private void Chat_Help(string args)
        {
            MyAPIGateway.Utilities.ShowMessage(Network.ModName, "\nSCORE: Displays the current score\nSAVE: saves the current state to disk");
        }

        private void ClientCallback_Update(ulong steamId, string commandString, byte[] data)
        {
            Update content = MyAPIGateway.Utilities.SerializeFromBinary<Update>(data);

            foreach (ZoneDescription zd in content.Zones)
            {
                //MyVisualScriptLogicProvider.SendChatMessage("clientCallback_update");
                ZoneBlock zone = Zones.Find(z =>
                    z.Entity.EntityId == zd.BlockId && z.ModBlock.CubeGrid.EntityId == zd.GridId);

                if (zone != null)
                {
                    zone.SetZone(zd);
                }
            }
        }
        //this is Not working
        private void ClientCallback_Score(ulong steamId, string commandString, byte[] data)
        {
            MyAPIGateway.Utilities.ShowMissionScreen(DisplayName, "King of the Hill", "",
                ASCIIEncoding.ASCII.GetString(data));
            //MyVisualScriptLogicProvider.SendChatMessage("clientCallback_Score");
        }
        //this Not is working 
        private void ClientCallback_SyncZone(ulong steamId, string commandString, byte[] data)
        {
            ZoneDescription zd = MyAPIGateway.Utilities.SerializeFromBinary<ZoneDescription>(data);

            ZoneBlock zone = Zones.Find(z =>
                z.Entity.EntityId == zd.BlockId && z.ModBlock.CubeGrid.EntityId == zd.GridId);
            zone.SetZone(zd);
            //MyVisualScriptLogicProvider.SendChatMessage("servercallback sync zone");
        }
        //this is Not working
        private void ServerCallback_Update(ulong steamId, string commandString, byte[] data)
        {
            Network.SendCommand("update", data: MyAPIGateway.Utilities.SerializeToBinary(GenerateUpdate()),
                steamId: steamId);
            //MyVisualScriptLogicProvider.SendChatMessage("servercallback update");
        }
        //This is working 
        private void ServerCallback_SyncZone(ulong steamId, string commandString, byte[] data)
        {
            ZoneDescription zd = MyAPIGateway.Utilities.SerializeFromBinary<ZoneDescription>(data);

            ZoneBlock zone = Zones.Find(z =>
                z.Entity.EntityId == zd.BlockId && z.ModBlock.CubeGrid.EntityId == zd.GridId);
            zone.SetZone(zd);

            Network.SendCommand("sync_zone", data: MyAPIGateway.Utilities.SerializeToBinary(zone.Data));
            //MyVisualScriptLogicProvider.SendChatMessage("servercallback synczone");
        }
        //this is Not working
        private void ServerCallback_Score(ulong steamId, string commandString, byte[] data)
        {
            StringBuilder formatedScores = new StringBuilder();
            foreach (var world in Worlds)
            {
                foreach (ScoreDescription score in world.Scores)
                {
                    formatedScores.AppendLine($"[{score.FactionTag}] {score.FactionName}: {score.Points.ToString()}");
                }
            }

            Network.SendCommand("score", data: ASCIIEncoding.ASCII.GetBytes(FormatScores()), steamId: steamId);
        }

        private void ServerCallback_Save(ulong steamId, string commandString, byte[] data)
        {
            if (Tools.IsAllowedSpecialOperations(steamId))
            {
                SaveData();
                Network.SendCommand("blank_message", "KotH Saved.", steamId: steamId);
            }
            else
            {
                Network.SendCommand("blank_message", "Requires admin rights", steamId: steamId);
            }
        }

        private void ServerCallback_Clear(ulong steamId, string commandString, byte[] data)
        {
            if (Tools.IsAllowedSpecialOperations(steamId))
            {
                Clearscore();
                Network.SendCommand("blank_message", "KotH score reset.", steamId: steamId);
            }
            else
            {
                Network.SendCommand("blank_message", "Requires admin rights", steamId: steamId);
            }
        }

        private void ServerCallback_ForceLoad(ulong steamId, string commandString, byte[] data)
        {
            if (Tools.IsAllowedSpecialOperations(steamId))
            {
                LoadData();
                Network.SendCommand("blank_message", "Scores force loaded", steamId: steamId);
            }
            else
            {
                Network.SendCommand("blank_message", "Requires admin rights", steamId: steamId);
            }
        }

        #endregion
    }
}