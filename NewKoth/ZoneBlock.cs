using KingOfTheHill.Descriptions;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using System;
using System.Collections.Generic;
using System.Text;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace KingOfTheHill
{
    public enum ZoneStates { Active, Idle, Contested }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Beacon), false, "ZoneBlock", "ZoneBlockA")]
    public class ZoneBlock : MyGameLogicComponent
    {
        public ZoneDescription Data { get; private set; }
       
        /// <summary>
        /// This identifies how progress is calculated
        /// </summary>
        public ZoneStates State { get; private set; } = ZoneStates.Idle;

        /// <summary>
        /// Access to Keens block
        /// </summary>
        public IMyBeacon ModBlock { get; private set; }

        public string ComponentListBoxOutputString;

        private IMyFaction controlledByFaction = null;
        public IMyFaction ControlledByFaction
        {
            get { return controlledByFaction; }
            set
            {
                controlledByFaction = value;

                if (Data != null)
                {
                    Data.ControlledBy = (value == null) ? 0 : value.FactionId;
                }
            }

        }

        /// <summary>
        /// Signal for points to be awarded
        /// </summary>
        public static event Action<ZoneBlock, IMyFaction, int, long, ZoneDescription> OnAwardPoints = delegate { };

        public static event Action<ZoneBlock, IMyPlayer, IMyFaction> OnPlayerDied = delegate { };

        public static event Action<ZoneBlock> OnUpdate = delegate { };
        
        private Dictionary<long, int> ActiveEnemiesPerFaction = new Dictionary<long, int>();
        private bool IsInitialized = false;
        private int lastPlayerCount = 0;
        private ZoneStates lastState = ZoneStates.Idle;

        private List<IMySlimBlock> temp = new List<IMySlimBlock>();

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            NeedsUpdate = MyEntityUpdateEnum.EACH_FRAME;
            ModBlock = Entity as IMyBeacon;

            Data = ZoneDescription.Load(Entity);
            if (Data == null)
            {
                Tools.Log(MyLogSeverity.Warning, $"The data saved for {Entity.EntityId} returned null. loading defaults");
                Data = ZoneDescription.GetDefaultSettings();
            }
            Data.BlockId = ModBlock.EntityId;
            Data.GridId = ModBlock.CubeGrid.EntityId;

            Core.RegisterZone(this);
        }
       

        public void SetZone(ZoneDescription zone)
        {
            float progress = Data.Progress;
            long controlledBy = Data.ControlledBy;
            
            Data = zone;

            if (MyAPIGateway.Multiplayer.IsServer)
            {
                Data.BlockId = ModBlock.EntityId;
                Data.GridId = ModBlock.CubeGrid.EntityId;
                Data.Progress = progress;
                Data.ControlledBy = controlledBy;
            }
        }

        public override void Close()
        {
            Core.UnRegisterZone(this);
        }

        public override void UpdateBeforeSimulation()
        {
            try
            {
                if (!IsInitialized)
                {
                    CreateControls();
                    IsInitialized = true;
                }

                if (!ModBlock.IsFunctional || !ModBlock.Enabled || !ModBlock.IsWorking) return; // if the block is incomplete or turned off
                MatrixD matrix = Entity.WorldMatrix;
                Vector3D location = matrix.Translation;

                IMyPlayer localPlayer = MyAPIGateway.Session.LocalHumanPlayer;

                List<IMyPlayer> players = new List<IMyPlayer>();
                List<IMyPlayer> playersInZone = new List<IMyPlayer>();
                List<IMyFaction> factionsInZone = new List<IMyFaction>();

                List<IMyPlayer> validatedPlayers = new List<IMyPlayer>(); // players that meet activation criteria
                Dictionary<long, int> validatedPlayerCountByFaction = new Dictionary<long, int>();
                List<IMyCubeGrid> validatedGrids = new List<IMyCubeGrid>();
                IMyFaction nominatedFaction = null;

                

                MyAPIGateway.Players.GetPlayers(players);

                foreach (IMyPlayer p in players)
                {
                    if (p.Character != null)
                    {
                        p.Character.CharacterDied -= Died;
                    }

                    if (Vector3D.Distance(p.GetPosition(), location) > Data.Radius) continue;
                    playersInZone.Add(p);

                    if (p.Character != null)
                    {
                        p.Character.CharacterDied += Died;
                    }

                    if (!Data.ActivateOnCharacter && !(p.Controller.ControlledEntity is IMyCubeBlock)) continue;

                    IMyFaction f = MyAPIGateway.Session.Factions.TryGetPlayerFaction(p.IdentityId);
                    if (f == null) continue;

                    validatedPlayers.Add(p);

                    if ((p.Controller.ControlledEntity is IMyCubeBlock))
                    {
                        IMyCubeBlock cube = (p.Controller.ControlledEntity as IMyCubeBlock);
                        IMyCubeGrid grid = cube.CubeGrid;

                        if (grid.IsStatic) continue;

                        if (!Data.ActivateOnUnpoweredGrid && !cube.IsWorking) continue;

                        if (!Data.ActivateOnCharacter)
                        {
                            if (grid.GridSizeEnum == MyCubeSize.Large)
                            {
                                if (!Data.ActivateOnLargeGrid)
                                {
                                    validatedPlayers.Remove(p);
                                    continue;
                                }

                                int blockCount = 0;
                                grid.GetBlocks(temp, (block) => { blockCount++; return false; });
                                if (blockCount < Data.MinLargeGridBlockCount)
                                {
                                    validatedPlayers.Remove(p);
                                    continue;
                                }
                            }
                            else if (grid.GridSizeEnum == MyCubeSize.Small)
                            {
                                if (!Data.ActivateOnSmallGrid)
                                {
                                    validatedPlayers.Remove(p);
                                    continue;
                                }

                                int blockCount = 0;
                                grid.GetBlocks(temp, (block) => { blockCount++; return false; });
                                if (blockCount < Data.MinSmallGridBlockCount)
                                {
                                    validatedPlayers.Remove(p);
                                    continue;
                                }
                            }
                        }

                        if (Data.IgnoreCopilot)
                        {
                            if (validatedGrids.Contains(grid))
                            {
                                validatedPlayers.Remove(p);
                                continue;
                            }
                            else
                            {
                                validatedGrids.Add(grid);
                            }
                        }
                    }

                    if (nominatedFaction == null)
                    {
                        nominatedFaction = f;
                    }

                    if (!ActiveEnemiesPerFaction.ContainsKey(f.FactionId))
                    {
                        ActiveEnemiesPerFaction.Add(f.FactionId, 0);
                    }

                    if (!validatedPlayerCountByFaction.ContainsKey(f.FactionId))
                    {
                        validatedPlayerCountByFaction.Add(f.FactionId, 1);
                        factionsInZone.Add(f);
                    }
                    else
                    {
                        validatedPlayerCountByFaction[f.FactionId]++;
                    }
                }

                bool isContested = false;
                for (int i = 0; i < factionsInZone.Count; i++)
                {
                    for (int j = 0; j < factionsInZone.Count; j++)
                    {
                        if (factionsInZone[i] == factionsInZone[j]) continue;

                        if (MyAPIGateway.Session.Factions.GetRelationBetweenFactions(factionsInZone[i].FactionId, factionsInZone[j].FactionId) == MyRelationsBetweenFactions.Enemies)
                        {
                            isContested = true;
                            break;
                        }
                    }
                }

                int factionCount = validatedPlayerCountByFaction.Keys.Count;
                Color color = Color.Gray;
                lastState = State;

                float speed = 0;
                if (isContested)
                {
                    State = ZoneStates.Contested;
                    color = Color.Orange;
                    speed = -GetProgress(Data.ContestedDrainRate);
                    Data.Progress += speed;

                    if (ControlledByFaction == null)
                    {
                        ControlledByFaction = nominatedFaction;
                    }
                }
                else if (factionCount == 0)
                {
                    State = ZoneStates.Idle;
                    ControlledByFaction = null;
                    speed = -GetProgress(Data.IdleDrainRate);
                    Data.Progress += speed;
                }
                else
                {
                    State = ZoneStates.Active;
                    color = Color.White;
                    speed = GetProgress(validatedPlayers.Count);
                    Data.Progress += speed;
                    ControlledByFaction = nominatedFaction;

                    foreach (IMyFaction zoneFaction in factionsInZone)
                    {
                        int enemyCount = 0;
                        foreach (IMyPlayer p in players)
                        {
                            IMyFaction f = MyAPIGateway.Session.Factions.TryGetPlayerFaction(p.IdentityId);

                            if (f == null || f.FactionId == zoneFaction.FactionId) continue;

                            if (MyAPIGateway.Session.Factions.GetRelationBetweenFactions(f.FactionId, zoneFaction.FactionId) == MyRelationsBetweenFactions.Enemies)
                            {
                                enemyCount++;
                            }
                        }

                        if (ActiveEnemiesPerFaction[zoneFaction.FactionId] < enemyCount)
                        {
                            ActiveEnemiesPerFaction[zoneFaction.FactionId] = enemyCount;
                        }
                    }
                }

                if (Data.Progress >= Data.ProgressWhenComplete)
                {
                    //MyVisualScriptLogicProvider.SendChatMessage("onawardpoint");
                    OnAwardPoints.Invoke(this, ControlledByFaction, ActiveEnemiesPerFaction[ControlledByFaction.FactionId], Data.GridId, Data);
                    //MyVisualScriptLogicProvider.SendChatMessage(" afteronawardpoint");
                    ResetActiveEnemies();
                    Data.Progress = 0;
                }

                if (Data.Progress <= 0)
                {
                    Data.Progress = 0;
                }

                // display info
                //MyVisualScriptLogicProvider.SendChatMessage("above if myapi");
                if (MyAPIGateway.Multiplayer.IsServer)
                {
                    //MyVisualScriptLogicProvider.SendChatMessage("multiplayer");
                    int percent = (int)Math.Floor((Data.Progress / Data.ProgressWhenComplete) * 100f);
                    //MyVisualScriptLogicProvider.SendChatMessage("Data.progress/Data.Progress");
                    ModBlock.CustomName = $"{State.ToString().ToUpper()} - {percent}% {(State != ZoneStates.Idle ? $"[{ControlledByFaction.Tag}]" : "")}";

                    if (lastState != State)
                    {
                        //MyVisualScriptLogicProvider.SendChatMessage("last state != state");
                        MyAPIGateway.Utilities.SendModMessage(Tools.ModMessageId, $"KotH: {ModBlock.CustomName}");
                    }
                }

                if (localPlayer != null && playersInZone.Contains(localPlayer))
                {
                    int allies = 0;
                    int enemies = 0;
                    int neutral = 0;
                    foreach (IMyPlayer p in playersInZone)
                    {
                        if (!Data.ActivateOnCharacter && !(p.Controller.ControlledEntity is IMyCubeBlock)) continue;

                        switch (localPlayer.GetRelationTo(p.IdentityId))
                        {
                            case MyRelationsBetweenPlayerAndBlock.Owner:
                            case MyRelationsBetweenPlayerAndBlock.FactionShare:
                                allies++;
                                break;
                            case MyRelationsBetweenPlayerAndBlock.Neutral:
                                neutral++;
                                break;
                            case MyRelationsBetweenPlayerAndBlock.Enemies:
                            case MyRelationsBetweenPlayerAndBlock.NoOwnership:
                                enemies++;
                                break;
                        }
                    }

                    string specialColor = "White";
                    switch (State)
                    {
                        case ZoneStates.Contested:
                            specialColor = "Red";
                            break;
                        case ZoneStates.Active:
                            specialColor = "Blue";
                            break;
                    }
                    MyAPIGateway.Utilities.ShowNotification($"Allies: {allies}  Neutral: {neutral}  Enemies: {enemies} - {State.ToString().ToUpper()}: {((Data.Progress / Data.ProgressWhenComplete) * 100).ToString("n0")}% Speed: {speed * 100}% {(ControlledByFaction != null ? $"Controlled By: {ControlledByFaction.Tag}" : "")}", 1, specialColor);
                }

                if (!MyAPIGateway.Utilities.IsDedicated)
                {
                    color.A = (byte)Data.Opacity;
                    MySimpleObjectDraw.DrawTransparentSphere(ref matrix, Data.Radius, ref color, MySimpleObjectRasterizer.Solid, 20, null, MyStringId.GetOrCompute("KothTransparency"), 0.12f, -1, null);
                }

                if (MyAPIGateway.Multiplayer.IsServer && playersInZone.Count != lastPlayerCount)
                {
                    //MyVisualScriptLogicProvider.SendChatMessage("before invote OnUpDate");
                    lastPlayerCount = playersInZone.Count;
                    OnUpdate.Invoke(this);
                    //MyVisualScriptLogicProvider.SendChatMessage("after invoke onUpDate");
                }
            }
            catch (Exception e)
            {
                Tools.Log(MyLogSeverity.Error, e.ToString());
            }
        }

        private void Died(IMyCharacter character)
        {
            List<IMyPlayer> players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players);

            foreach (IMyPlayer p in players)
            {
                if (p.Character == character)
                {
                    IMyFaction f = MyAPIGateway.Session.Factions.TryGetPlayerFaction(p.IdentityId);
                    if (f != null)
                    {
                        OnPlayerDied.Invoke(this, p, f);
                    }

                    break;
                }
            }

        }

        private float GetProgress(float progressModifier)
        {
            return (((float)progressModifier * (float)progressModifier - 1) / ((float)progressModifier * (float)progressModifier + (3 * (float)progressModifier) + 1)) + 1;
        }

        private void ResetActiveEnemies()
        {
            Dictionary<long, int> newDict = new Dictionary<long, int>();

            foreach (long key in ActiveEnemiesPerFaction.Keys)
            {
                newDict.Add(key, 0);
            }

            ActiveEnemiesPerFaction = newDict;
        }
        
        private void ComponentPrize(IMyTerminalBlock arg1, List<MyTerminalControlListBoxItem> arg2, List<MyTerminalControlListBoxItem> arg3)
        {
            try
            {
                List<string> ComponentPrizeList = new List<string>();
                ComponentPrizeList.Add("Construction");
                ComponentPrizeList.Add("MetalGrid");
                ComponentPrizeList.Add("InteriorPlate");
                ComponentPrizeList.Add("SteelPlate");
                ComponentPrizeList.Add("Girder");
                ComponentPrizeList.Add("SmallTube");
                ComponentPrizeList.Add("LargeTube");
                ComponentPrizeList.Add("Motor");
                ComponentPrizeList.Add("Display");
                ComponentPrizeList.Add("BulletproofGlass");
                ComponentPrizeList.Add("Superconductor");
                ComponentPrizeList.Add("Computer");
                ComponentPrizeList.Add("Reactor");
                ComponentPrizeList.Add("Thrust");
                ComponentPrizeList.Add("GravityGenerator");
                ComponentPrizeList.Add("Medical");
                ComponentPrizeList.Add("RadioCommunication");
                ComponentPrizeList.Add("Detector");
                ComponentPrizeList.Add("Explosives");
                ComponentPrizeList.Add("SolarCell");
                ComponentPrizeList.Add("PowerCell");
                ComponentPrizeList.Add("Canvas");

                string objectText = "abc";
                var dummy = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute("-Select Component Below-"), MyStringId.GetOrCompute("-Select Component Below-"), objectText);
                arg2.Add(dummy);
                
                foreach (var name in ComponentPrizeList) {
                    
                    var toList = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(name), MyStringId.GetOrCompute(name), objectText);
                    if (string.IsNullOrEmpty(name));
                    {
                        var placeholder = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute("placeholder"), MyStringId.GetOrCompute("placeholder"), Data.ComponentListBoxOutputString);
                        arg3.Add(placeholder);
                    }
                    
                    arg2.Add(toList);
                }

            }
            catch (Exception ex)
            {
                VRage.Utils.MyLog.Default.WriteLineAndConsole($"list error when setting up ComponentPrize {ex}");
            }
            
        }
        
        private void SelectedComponent(IMyTerminalBlock arg1, List<MyTerminalControlListBoxItem> arg2)
        {
            try
            {
                Data.ComponentListBoxOutputString = arg2[0].Text.ToString();
                
            }
            catch (Exception ex)
            {
                VRage.Utils.MyLog.Default.WriteLineAndConsole($"list error when setting up SelectedComponent {ex}");
            }
        }
        
        private void OrePrize(IMyTerminalBlock arg4, List<MyTerminalControlListBoxItem> arg5, List<MyTerminalControlListBoxItem> arg6)
        {
            try
            {
                List<string> OrePrizeList = new List<string>();
                OrePrizeList.Add("Stone");
                OrePrizeList.Add("Iron");
                OrePrizeList.Add("Nickel");
                OrePrizeList.Add("Cobalt");
                OrePrizeList.Add("Magnesium");
                OrePrizeList.Add("Silicon");
                OrePrizeList.Add("Silver");
                OrePrizeList.Add("Gold");
                OrePrizeList.Add("Platinum");
                OrePrizeList.Add("Uranium");

                string objectText = "abc";
                var dummy = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute("-Select Ore Below-"), MyStringId.GetOrCompute("-Select Ore Below-"), objectText);
                arg5.Add(dummy);
                
                foreach (var name in OrePrizeList) {
                    
                    var toList = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(name), MyStringId.GetOrCompute(name), objectText);
                    if (string.IsNullOrEmpty(name));
                    {
                        arg6.Add(toList);
                    }
                    
                    arg5.Add(toList);
                }

            }
            catch (Exception ex)
            {
                VRage.Utils.MyLog.Default.WriteLineAndConsole($"list error when setting up OrePrize {ex}");
            }
            
        }
        
        private void SelectedOre(IMyTerminalBlock arg4, List<MyTerminalControlListBoxItem> arg5)
        {
            try
            {
                Data.OreListBoxOutputString = arg5[0].Text.ToString();
                
            }
            catch (Exception ex)
            {
                VRage.Utils.MyLog.Default.WriteLineAndConsole($"list error when setting up SelectedComponent {ex}");
            }
        }
        
        private void IngotPrize(IMyTerminalBlock arg7, List<MyTerminalControlListBoxItem> arg8, List<MyTerminalControlListBoxItem> arg9)
        {
            try
            {
                List<string> IngotPrizeList = new List<string>();
                IngotPrizeList.Add("Stone");
                IngotPrizeList.Add("Iron");
                IngotPrizeList.Add("Nickel");
                IngotPrizeList.Add("Cobalt");
                IngotPrizeList.Add("Magnesium");
                IngotPrizeList.Add("Silicon");
                IngotPrizeList.Add("Silver");
                IngotPrizeList.Add("Gold");
                IngotPrizeList.Add("Platinum");
                IngotPrizeList.Add("Uranium");

                string objectText = "abc";
                var dummy = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute("-Select Ingot Below-"), MyStringId.GetOrCompute("-Select Ingot Below-"), objectText);
                arg8.Add(dummy);
                
                foreach (var name in IngotPrizeList) {
                    
                    var toList = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(name), MyStringId.GetOrCompute(name), objectText);
                    if (string.IsNullOrEmpty(name));
                    {
                        arg9.Add(toList);
                    }
                    
                    arg8.Add(toList);
                }

            }
            catch (Exception ex)
            {
                VRage.Utils.MyLog.Default.WriteLineAndConsole($"list error when setting up IngotPrize {ex}");
            }
            
        }
        
        private void SelectedIngot(IMyTerminalBlock arg7, List<MyTerminalControlListBoxItem> arg8)
        {
            try
            {
                Data.IngotListBoxOutputString = arg8[0].Text.ToString();
                
            }
            catch (Exception ex)
            {
                VRage.Utils.MyLog.Default.WriteLineAndConsole($"list error when setting up SelectedComponent {ex}");
            }
        }

        private void CreateControls()
        {
            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                IMyTerminalControlSlider Slider = null;
                IMyTerminalControlCheckbox Checkbox = null;               
                IMyTerminalControlTextbox textbox = null;
                IMyTerminalControlSeparator separator = null;
                IMyTerminalControlLabel label = null;

                label =
                    MyAPIGateway.TerminalControls
                        .CreateControl<IMyTerminalControlLabel, IMyBeacon>("zone_KothZoneSettingsLabel");
                label.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                label.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };
                
                label.Label = MyStringId.GetOrCompute("Koth Zone Settings");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(label);
                
                separator =
                    MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyBeacon>(
                        "zone_KothZoneSeparator");
                separator.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                separator.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(separator);

                Checkbox = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyBeacon>("Zone_ActivateOnCharacter");
                Checkbox.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                Checkbox.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Checkbox.Setter = (block, value) => {
                    Data.ActivateOnCharacter = value;
                    OnUpdate.Invoke(this);
                    UpdateControls();
                };
                Checkbox.Getter = (block) => Data.ActivateOnCharacter;
                Checkbox.Title = MyStringId.GetOrCompute("Activate On Character");
                Checkbox.Tooltip = MyStringId.GetOrCompute("Only requires a player to activate the zone");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Checkbox);

                Checkbox = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyBeacon>("Zone_ActivateOnSmallGrid");
                Checkbox.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && !Data.ActivateOnCharacter; };
                Checkbox.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Checkbox.Setter = (block, value) => { Data.ActivateOnSmallGrid = value; OnUpdate.Invoke(this); };
                Checkbox.Getter = (block) => Data.ActivateOnSmallGrid;
                Checkbox.Title = MyStringId.GetOrCompute("Activate On Small Grid");
                Checkbox.Tooltip = MyStringId.GetOrCompute("Will activate if a piloted small grid is in the zone");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Checkbox);

                Checkbox = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyBeacon>("Zone_ActivateOnLargeGrid");
                Checkbox.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && !Data.ActivateOnCharacter; };
                Checkbox.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Checkbox.Setter = (block, value) => { Data.ActivateOnLargeGrid = value; OnUpdate.Invoke(this); };
                Checkbox.Getter = (block) => Data.ActivateOnLargeGrid;
                Checkbox.Title = MyStringId.GetOrCompute("Activate On Large Grid");
                Checkbox.Tooltip = MyStringId.GetOrCompute("Will activate if a piloted large grid is in the zone");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Checkbox);

                Checkbox = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyBeacon>("Zone_ActivateOnUnpoweredGrid");
                Checkbox.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && !Data.ActivateOnCharacter; };
                Checkbox.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Checkbox.Setter = (block, value) => { Data.ActivateOnUnpoweredGrid = value; OnUpdate.Invoke(this); };
                Checkbox.Getter = (block) => Data.ActivateOnUnpoweredGrid;
                Checkbox.Title = MyStringId.GetOrCompute("Activate On Unpowered Grid");
                Checkbox.Tooltip = MyStringId.GetOrCompute("Will activate if piloted grid is unpowered");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Checkbox);

                Checkbox = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyBeacon>("Zone_IgnoreCopilot");
                Checkbox.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                Checkbox.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Checkbox.Setter = (block, value) => { Data.IgnoreCopilot = value; OnUpdate.Invoke(this); };
                Checkbox.Getter = (block) => Data.IgnoreCopilot;
                Checkbox.Title = MyStringId.GetOrCompute("Ignore Copilot");
                Checkbox.Tooltip = MyStringId.GetOrCompute("Will count a copiloted grid as a single person");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Checkbox);

                Slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyBeacon>("Zone_MinSmallGridBlockCount");
                Slider.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && !Data.ActivateOnCharacter; };
                Slider.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Slider.Setter = (block, value) => { Data.MinSmallGridBlockCount = (int)value; OnUpdate.Invoke(this); };
                Slider.Getter = (block) => Data.MinSmallGridBlockCount;
                Slider.Writer = (block, value) => value.Append($"{Data.MinSmallGridBlockCount} blocks");
                Slider.Title = MyStringId.GetOrCompute("SmallGrid min blocks");
                Slider.Tooltip = MyStringId.GetOrCompute("The minimum blocks considered an activation grid");
                Slider.SetLimits(Constants.MinBlockCount, Constants.MaxBlockCount);
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Slider);

                Slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyBeacon>("Zone_MinLargeGridBlockCount");
                Slider.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && !Data.ActivateOnCharacter; };
                Slider.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Slider.Setter = (block, value) => { Data.MinLargeGridBlockCount = (int)value; OnUpdate.Invoke(this); };
                Slider.Getter = (block) => Data.MinLargeGridBlockCount;
                Slider.Writer = (block, value) => value.Append($"{Data.MinLargeGridBlockCount} blocks");
                Slider.Title = MyStringId.GetOrCompute("LargeGrid min blocks");
                Slider.Tooltip = MyStringId.GetOrCompute("The minimum blocks considered an activation grid");
                Slider.SetLimits(Constants.MinBlockCount, Constants.MaxBlockCount);
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Slider);

                Slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyBeacon>("Zone_Opacity");
                Slider.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                Slider.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Slider.Setter = (block, value) => { Data.Opacity = value; OnUpdate.Invoke(this); };
                Slider.Getter = (block) => Data.Opacity;
                Slider.Writer = (block, value) => value.Append($"{Data.Opacity} alpha");
                Slider.Title = MyStringId.GetOrCompute("Opacity");
                Slider.Tooltip = MyStringId.GetOrCompute("Sphere visiblility");
                Slider.SetLimits(0, 255);
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Slider);
                
                Slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyBeacon>("Zone_Radius");
                Slider.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                Slider.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Slider.Setter = (block, value) => { Data.Radius = value; OnUpdate.Invoke(this); };
                Slider.Getter = (block) => Data.Radius;
                Slider.Writer = (block, value) => value.Append($"{Math.Round(Data.Radius, 0)}m");
                Slider.Title = MyStringId.GetOrCompute("Radius");
                Slider.Tooltip = MyStringId.GetOrCompute("Capture Zone Radius");
                Slider.SetLimits(Constants.MinRadius, Constants.MaxRadius);
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Slider);               

                Slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyBeacon>("Zone_ProgressWhenComplete");
                Slider.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                Slider.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Slider.Setter = (block, value) => { Data.ProgressWhenComplete = value; OnUpdate.Invoke(this); };
                Slider.Getter = (block) => Data.ProgressWhenComplete;
                Slider.Writer = (block, value) => value.Append($"{TimeSpan.FromMilliseconds((Data.ProgressWhenComplete / 60) * 1000).ToString("g").Split('.')[0]}");
                Slider.Title = MyStringId.GetOrCompute("Capture Time");
                Slider.Tooltip = MyStringId.GetOrCompute("The base capture time");
                Slider.SetLimits(Constants.MinCaptureTime, Constants.MaxCaptureTime);
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Slider);

                Slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyBeacon>("Zone_IdleDrainRate");
                Slider.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                Slider.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Slider.Setter = (block, value) => { Data.IdleDrainRate = value; OnUpdate.Invoke(this); };
                Slider.Getter = (block) => Data.IdleDrainRate;
                Slider.Writer = (block, value) => value.Append($"{Math.Round(Data.IdleDrainRate * 100, 0)}%");
                Slider.Title = MyStringId.GetOrCompute("Idle Drain Rate");
                //Slider.Tooltip = MyStringId.GetOrCompute("How quickly the ");
                Slider.SetLimits(0, 5);
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Slider);

                Slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyBeacon>("Zone_ContestedDrainRate");
                Slider.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                Slider.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Slider.Setter = (block, value) => { Data.ContestedDrainRate = value; OnUpdate.Invoke(this); };
                Slider.Getter = (block) => Data.ContestedDrainRate;
                Slider.Writer = (block, value) => value.Append($"{Math.Round(Data.ContestedDrainRate * 100, 0)}%");
                Slider.Title = MyStringId.GetOrCompute("Contested Drain Rate");
                //Slider.Tooltip = MyStringId.GetOrCompute("The minimum blocks considered an activation grid");
                Slider.SetLimits(0, 5);
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Slider);

                Slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyBeacon>("Zone_PointsRemovedOnDeath");
                Slider.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                Slider.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Slider.Setter = (block, value) => { Data.PointsRemovedOnDeath = (int)value; OnUpdate.Invoke(this); };
                Slider.Getter = (block) => Data.PointsRemovedOnDeath;
                Slider.Writer = (block, value) => value.Append($"{Data.PointsRemovedOnDeath}");
                Slider.Title = MyStringId.GetOrCompute("Points Removed On Death");
                Slider.SetLimits(0, 1000);
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Slider);

                label =
                    MyAPIGateway.TerminalControls
                        .CreateControl<IMyTerminalControlLabel, IMyBeacon>("zone_PrizeLabel");
                label.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                label.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };
                
                label.Label = MyStringId.GetOrCompute("Koth Prize Settings");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(label);
                
                separator =
                    MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyBeacon>(
                        "zone_PrizeSeparator");
                separator.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                separator.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(separator);
                
                Slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyBeacon>("Zone_PointsForPrizes");
                Slider.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                Slider.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Slider.Setter = (block, value) => { Data.PointsForPrize = value; OnUpdate.Invoke(this); };
                Slider.Getter = (block) => Data.PointsForPrize;
                Slider.Writer = (block, value) => value.Append($"{Math.Round(Data.PointsForPrize, 0)}Points");
                Slider.Title = MyStringId.GetOrCompute("Points for Prizes");
                Slider.Tooltip = MyStringId.GetOrCompute("Points");
                Slider.SetLimits(Constants.MinPoints, Constants.MaxPoints);
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Slider);
                
                Checkbox = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyBeacon>("Zone_AwardPointsAsCredits");
                Checkbox.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                Checkbox.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Checkbox.Setter = (block, value) =>
                {
                    Data.AwardPointsAsCredits = value; OnUpdate.Invoke(this);
                    UpdateControls();
                };
                Checkbox.Getter = (block) => Data.AwardPointsAsCredits;
                Checkbox.Title = MyStringId.GetOrCompute("Award Points As Credits");
                Checkbox.Tooltip = MyStringId.GetOrCompute("Will deposit credit into the capping faction");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Checkbox);
                
                Checkbox = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyBeacon>("Zone_UseComponentReward");
                Checkbox.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                Checkbox.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Checkbox.Setter = (block, value) =>
                {
                    Data.UseComponentReward = value; OnUpdate.Invoke(this);
                    UpdateControls();
                };
                Checkbox.Getter = (block) => Data.UseComponentReward;
                Checkbox.Title = MyStringId.GetOrCompute("Component Reward");
                Checkbox.Tooltip = MyStringId.GetOrCompute("Prize will be components");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Checkbox);

                Checkbox = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyBeacon>("Zone_UseOreReward");
                Checkbox.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                Checkbox.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Checkbox.Setter = (block, value) =>
                {   
                    Data.UseOreReward = value; OnUpdate.Invoke(this);
                    UpdateControls();
                };
                
                Checkbox.Getter = (block) => Data.UseOreReward;
                Checkbox.Title = MyStringId.GetOrCompute("Ore Reward");
                Checkbox.Tooltip = MyStringId.GetOrCompute("Prize will be ore");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Checkbox);

                Checkbox = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyBeacon>("Zone_UseIngotReward");
                Checkbox.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                Checkbox.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Checkbox.Setter = (block, value) =>
                {
                    Data.UseIngotReward = value; OnUpdate.Invoke(this);
                    UpdateControls();
                };
                Checkbox.Getter = (block) => Data.UseIngotReward;
                Checkbox.Title = MyStringId.GetOrCompute("Ingot Reward");
                Checkbox.Tooltip = MyStringId.GetOrCompute("Prize will be Ingot");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Checkbox);

                Slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyBeacon>("Zone_SpaceCreditRewardAmount");
                Slider.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && Data.AwardPointsAsCredits; };
                Slider.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Slider.Setter = (block, value) => { Data.SpaceCreditRewardAmount = value; OnUpdate.Invoke(this);};
                Slider.Getter = (block) => Data.SpaceCreditRewardAmount;
                Slider.Writer = (block, value) => value.Append($"{Math.Round(Data.SpaceCreditRewardAmount, 0)}$");
                Slider.Title = MyStringId.GetOrCompute("Amount of credits for prize");
                Slider.Tooltip = MyStringId.GetOrCompute("Credits");
                Slider.SetLimits(Constants.MinCredit, Constants.MaxCredit);
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Slider);

                Slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyBeacon>("Zone_PrizeAmount");
                Slider.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && Data.UseComponentReward; };
                Slider.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Slider.Setter = (block, value) => { Data.PrizeAmount = value; OnUpdate.Invoke(this); };
                Slider.Getter = (block) => Data.PrizeAmount;
                Slider.Writer = (block, value) => value.Append($"{Math.Round(Data.PrizeAmount, 0)}#");
                Slider.Title = MyStringId.GetOrCompute("Prize Amount Components");
                Slider.Tooltip = MyStringId.GetOrCompute("Number of Prizes");
                Slider.SetLimits(Constants.MinAmount, Constants.MaxAmount);
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Slider);

                Slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyBeacon>("Zone_PrizeAmountOre");
                Slider.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && Data.UseOreReward; };
                Slider.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Slider.Setter = (block, value) => { Data.PrizeAmountOre = value; OnUpdate.Invoke(this); };
                Slider.Getter = (block) => Data.PrizeAmountOre;
                Slider.Writer = (block, value) => value.Append($"{Math.Round(Data.PrizeAmountOre, 0)}#");
                Slider.Title = MyStringId.GetOrCompute("Prize Amount ore");
                Slider.Tooltip = MyStringId.GetOrCompute("Number of Prizes");
                Slider.SetLimits(Constants.MinAmount, Constants.MaxAmount);
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Slider);

                Slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyBeacon>("Zone_PrizeAmountIngot");
                Slider.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && Data.UseIngotReward; };
                Slider.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Slider.Setter = (block, value) => { Data.PrizeAmountIngot = value; OnUpdate.Invoke(this); };
                Slider.Getter = (block) => Data.PrizeAmountIngot;
                Slider.Writer = (block, value) => value.Append($"{Math.Round(Data.PrizeAmountIngot, 0)}#");
                Slider.Title = MyStringId.GetOrCompute("Prize Amount Ingots");
                Slider.Tooltip = MyStringId.GetOrCompute("Number of Prizes");
                Slider.SetLimits(Constants.MinAmount, Constants.MaxAmount);
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Slider);

                var componentlist =
                    MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyBeacon>(
                        "Zone_ComponentList");
                componentlist.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && !Data.AdvancedComponentSelection && Data.UseComponentReward; };
                componentlist.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };
                
                componentlist.Title = MyStringId.GetOrCompute("Select Vanilla Components");
                componentlist.SupportsMultipleBlocks = false;
                componentlist.ListContent = ComponentPrize;
                componentlist.VisibleRowsCount = 6;
                componentlist.ItemSelected = SelectedComponent;
                componentlist.Multiselect = false;
                MyAPIGateway.TerminalControls.AddControl<IMyBeacon>(componentlist);
                
                Checkbox = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyBeacon>("Zone_AdvancedComponentOption");
                Checkbox.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && Data.UseComponentReward; };
                Checkbox.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Checkbox.Setter = (block, value) =>
                {
                    Data.AdvancedComponentSelection = value; OnUpdate.Invoke(this);
                    UpdateControls();
                };
                Checkbox.Getter = (block) => Data.AdvancedComponentSelection;
                Checkbox.Title = MyStringId.GetOrCompute("Advanced Component Selection");
                Checkbox.Tooltip = MyStringId.GetOrCompute("allows for manually adding modded Components");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Checkbox);

                var Orelist =
                    MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyBeacon>(
                        "Zone_OreList");
                Orelist.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && !Data.AdvancedOreSelection && Data.UseOreReward; };
                Orelist.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };
                
                Orelist.Title = MyStringId.GetOrCompute("Select Vanilla Ores");
                Orelist.SupportsMultipleBlocks = false;
                Orelist.ListContent = OrePrize;
                Orelist.VisibleRowsCount = 6;
                Orelist.ItemSelected = SelectedOre;
                Orelist.Multiselect = false;
                MyAPIGateway.TerminalControls.AddControl<IMyBeacon>(Orelist);
                
                Checkbox = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyBeacon>("Zone_AdvancedOreOption");
                Checkbox.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && Data.UseOreReward; };
                Checkbox.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Checkbox.Setter = (block, value) =>
                {
                    Data.AdvancedOreSelection = value; OnUpdate.Invoke(this);
                    UpdateControls();
                };
                Checkbox.Getter = (block) => Data.AdvancedOreSelection;
                Checkbox.Title = MyStringId.GetOrCompute("Advanced Ore Selection");
                Checkbox.Tooltip = MyStringId.GetOrCompute("allows for manually adding modded Ores");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Checkbox);
                
                
                
                
                var Ingotlist =
                    MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyBeacon>(
                        "Zone_IngotList");
                Ingotlist.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && !Data.AdvancedIngotSelection && Data.UseIngotReward; };
                Ingotlist.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };
                
                Ingotlist.Title = MyStringId.GetOrCompute("Select Vanilla Ingot");
                Ingotlist.SupportsMultipleBlocks = false;
                Ingotlist.ListContent = IngotPrize;
                Ingotlist.VisibleRowsCount = 6;
                Ingotlist.ItemSelected = SelectedIngot;
                Ingotlist.Multiselect = false;
                MyAPIGateway.TerminalControls.AddControl<IMyBeacon>(Ingotlist);
                
                Checkbox = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyBeacon>("Zone_AdvancedIngotOption");
                Checkbox.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && Data.UseIngotReward; };
                Checkbox.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                Checkbox.Setter = (block, value) =>
                {
                    Data.AdvancedIngotSelection = value; OnUpdate.Invoke(this);
                    UpdateControls();
                };
                Checkbox.Getter = (block) => Data.AdvancedIngotSelection;
                Checkbox.Title = MyStringId.GetOrCompute("Advanced Ingot Selection");
                Checkbox.Tooltip = MyStringId.GetOrCompute("allows for manually adding modded Ingot");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(Checkbox);
                
                
                
                

                label =
                    MyAPIGateway.TerminalControls
                        .CreateControl<IMyTerminalControlLabel, IMyBeacon>("zone_AdvanceLabel");
                label.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                label.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };
                
                label.Label = MyStringId.GetOrCompute("Advanced Prize Settings");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(label);
                
                separator =
                    MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyBeacon>(
                        "zone_PrizeSeparator");
                separator.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                separator.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(separator);
                
                textbox = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyBeacon>("zone_PrizeComponentSubtypeId");
                textbox.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && Data.AdvancedComponentSelection && Data.UseComponentReward; };
                textbox.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                textbox.Setter = (block, value) => { Data.PrizeComponentSubtypeId = ( value.ToString() ); OnUpdate.Invoke(this); };
                textbox.Getter = (block) => new StringBuilder(Data.PrizeComponentSubtypeId);
                textbox.Title = MyStringId.GetOrCompute("Component subtypeId");
                textbox.Tooltip = MyStringId.GetOrCompute("must use subtypeId");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(textbox);
                
                textbox = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyBeacon>("zone_PrizeOreSubtypeId");
                textbox.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && Data.AdvancedOreSelection && Data.UseOreReward; };
                textbox.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                textbox.Setter = (block, value) => { Data.PrizeOreSubtypeId = ( value.ToString() ); OnUpdate.Invoke(this); };
                textbox.Getter = (block) => new StringBuilder(Data.PrizeOreSubtypeId);
                textbox.Title = MyStringId.GetOrCompute("Ore subtypeId");
                textbox.Tooltip = MyStringId.GetOrCompute("must use subtypeId");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(textbox);
                
                textbox = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyBeacon>("zone_PrizeIngotSubtypeId");
                textbox.Enabled = (block) => { return block.EntityId == ModBlock.EntityId && Data.AdvancedIngotSelection && Data.UseIngotReward; };
                textbox.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };

                textbox.Setter = (block, value) => { Data.PrizeIngotSubtypeId = ( value.ToString() ); OnUpdate.Invoke(this); };
                textbox.Getter = (block) => new StringBuilder(Data.PrizeIngotSubtypeId);
                textbox.Title = MyStringId.GetOrCompute("Ingot subtypeId");
                textbox.Tooltip = MyStringId.GetOrCompute("must use subtypeId");
                MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyBeacon>(textbox);
            }
        }


        private void UpdateControls()
        {
            List<IMyTerminalControl> controls = new List<IMyTerminalControl>();
            MyAPIGateway.TerminalControls.GetControls<IMyBeacon>(out controls);

            foreach (IMyTerminalControl control in controls)
            {
                control.UpdateVisual();
            }
        }
    }
}
