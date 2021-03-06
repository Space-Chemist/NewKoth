﻿using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Security.Policy;
using System.Text;
using KingOfTheHill;
using Sandbox.Game;
using VRage.Scripting;
using VRage.Utils;
using KingOfTheHill.Descriptions;

namespace ModNetworkAPI
{
    public enum NetworkTypes { Dedicated, Server, Client }

    public abstract class NetworkAPI
    {
        public static NetworkAPI Instance = null;
        public static bool IsInitialized = Instance != null;
        

        /// <summary>
        /// Event triggers apon reciveing data over the network
        /// steamId, command, data
        /// </summary>
        public event Action<ulong, string, byte[]> OnCommandRecived;

        /// <summary>
        /// Event triggers apon client chat input starting with this mods Keyword
        /// </summary>
        [Obsolete("Use NetworkAPI.RegisterCommand to handle input")]
        public event Action<string> OnTerminalInput;

        /// <summary>
        /// returns the type of network user this is: dedicated, server, client
        /// </summary>
        public NetworkTypes NetworkType => GetNetworkType();

        public static ushort ComId;
        public readonly string Keyword;
        public readonly string ModName;

        internal bool UsingTextCommands => Keyword != null;

        internal Dictionary<string, Action<ulong, string, byte[]>> NetworkCommands = new Dictionary<string, Action<ulong, string, byte[]>>();
        internal Dictionary<string, Action<string>> ChatCommands = new Dictionary<string, Action<string>>();

        /// <summary>
        /// Event driven client, server syncing API. 
        /// </summary>
        /// <param name="comId">The communication channel this mod will listen on</param>
        /// <param name="modName">The title use for displaying chat messages</param>
        /// <param name="keyword">The string identifying a chat command</param>
        public NetworkAPI(ushort comId, string modName, string keyword = null)
        {
            ComId = comId;
            ModName = (modName == null) ? string.Empty : modName;
            Keyword = (keyword != null) ? keyword.ToLower() : null;

            if (UsingTextCommands)
            {
                MyAPIGateway.Utilities.MessageEntered += HandleChatInput;
            }

            MyAPIGateway.Multiplayer.RegisterMessageHandler(ComId, HandleIncomingPacket);

            MyLog.Default.Info($"[NetworkAPI] Initialized. ComId: {ComId} Name: {ModName} Keyword: {Keyword}");
        }
        
        public enum MessageType : byte
        {
            DeleteD = 0,
            RunF = 1,
            Message = 2
        }
        
        public void RegisterHandlers()
        {
            MyAPIGateway.Multiplayer.RegisterMessageHandler(Core.ComId, MessageHandler);
        }

        public static void UnregisterHandlers()
        {
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(Core.ComId, MessageHandler);
        }

        private static void MessageHandler(byte[] bytes)
        {
            try
            {
                var type = (MessageType)bytes[1];


                var data = new byte[bytes.Length - 1];
                Array.Copy(bytes, 1, data, 0, data.Length);

                switch (type)
                {
                    case MessageType.DeleteD:
                        DeleteD();
                        break;
                    case MessageType.Message:
                        Message(data);
                        break;
                    default:
                        return;
                }
            }
            catch (Exception ex)
            {
                
            }
        }
        
        [Serializable]
        public struct Package
        {
            public string Message;
        }
        
        public static void Message(byte[] data)
        {
            var package = MyAPIGateway.Utilities.SerializeFromXML<Package>(Encoding.UTF8.GetString(data));
            
        }

        public static void DeleteD()
        {
            Core.Clearscore();
            string message = "fuck you reload";
            var package = new Package
            {
                Message = message,
            };
            byte[] data = Encoding.UTF8.GetBytes(MyAPIGateway.Utilities.SerializeToXML(package));
            SendToServer(MessageType.RunF, data, ComId);
            
        }

        

        public void MessageToServer()
        {
            MyVisualScriptLogicProvider.SendChatMessage("function fired");
            string message = "fuck you reload";
            var package = new Package
            {
                Message = message,
            };
            byte[] data = Encoding.UTF8.GetBytes(MyAPIGateway.Utilities.SerializeToXML(package));
            SendToServer(MessageType.RunF, data, ComId);
            MyVisualScriptLogicProvider.SendChatMessage("before send to server");
        }

        public static void SendToServer(MessageType type, byte[] data, ushort comId)
        {
            MyVisualScriptLogicProvider.SendChatMessage("sending to server");
            ComId = comId;
            var newData = new byte[data.Length + 1];
            newData[1] = (byte)type;
            data.CopyTo(newData, 1);
            MyAPIGateway.Utilities.InvokeOnGameThread(() => { MyAPIGateway.Multiplayer.SendMessageToServer(ComId, newData); });
        }

        /// <summary>
        /// Invokes chat command events
        /// </summary>
        /// <param name="messageText">Chat message string</param>
        /// <param name="sendToOthers">should be shown normally in global chat</param>
        private void HandleChatInput(string messageText, ref bool sendToOthers)
        {
            string[] args = messageText.ToLower().Split(' ');
            if (args[0] != Keyword) return;
            sendToOthers = false;

            string arguments = messageText.Substring(Keyword.Length).Trim(' ');
            OnTerminalInput?.Invoke(arguments);

            // Meh... this is kinda yucky
            if (args.Length == 1 && ChatCommands.ContainsKey(string.Empty))
            {
                ChatCommands[string.Empty]?.Invoke(string.Empty);
            }
            else if (args.Length > 1 && ChatCommands.ContainsKey(args[1]))
            {
                ChatCommands[args[1]]?.Invoke(arguments.Substring(args[1].Length).Trim(' '));
            }
            else
            {
                if (NetworkType != NetworkTypes.Dedicated)
                {
                    MyAPIGateway.Utilities.ShowMessage(ModName, "Command not recognized.");
                }
            }
        }

        /// <summary>
        /// Unpacks commands and handles arguments
        /// </summary>
        /// <param name="msg">Data chunck recived from the network</param>
        private void HandleIncomingPacket(byte[] msg)
        {
            try
            {
                Command cmd = MyAPIGateway.Utilities.SerializeFromBinary<Command>(msg);

                if (!string.IsNullOrWhiteSpace(cmd.Message) && NetworkType == NetworkTypes.Client && MyAPIGateway.Session != null)
                {
                    MyAPIGateway.Utilities.ShowMessage(ModName, cmd.Message);
                }

                if (cmd != null)
                {
                    OnCommandRecived?.Invoke(cmd.SteamId, cmd.CommandString, cmd.Data);
                }

                if (cmd.CommandString == null)
                {
                    cmd.CommandString = string.Empty;
                }

                string command = cmd.CommandString.Split(' ')[0];

                if (NetworkCommands.ContainsKey(command))
                {
                    NetworkCommands[command]?.Invoke(cmd.SteamId, cmd.CommandString, cmd.Data);
                }

            }
            catch (Exception e)
            {
                
            }
        }

        /// <summary>
        /// Registers a callback that will fire when the command string is sent
        /// </summary>
        /// <param name="command">The command that triggers the callback</param>
        /// <param name="callback">The function that runs when a command is recived</param>
        public void RegisterNetworkCommand(string command, Action<ulong, string, byte[]> callback)
        {
            if (command == null)
            {
                command = string.Empty;
            }

            command = command.ToLower();

            if (NetworkCommands.ContainsKey(command))
            {
                throw new Exception($"[NetworkAPI] Failed to add the network command callback '{command}'. A command with the same name was already added.");
            }

            NetworkCommands.Add(command, callback);
        }

        /// <summary>
        /// Unregisters a command
        /// </summary>
        /// <param name="command"></param>
        public void UnregisterNetworkCommand(string command)
        {
            if (NetworkCommands.ContainsKey(command))
            {
                NetworkCommands.Remove(command);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="command"></param>
        /// <param name="callback"></param>
        public void RegisterChatCommand(string command, Action<string> callback)
        {
            if (command == null)
            {
                command = string.Empty;
            }

            command = command.ToLower();

            if (ChatCommands.ContainsKey(command))
            {
                throw new Exception($"[NetworkAPI] Failed to add the network command callback '{command}'. A command with the same name was already added.");
            }

            ChatCommands.Add(command, callback);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="command"></param>
        /// <param name="callback"></param>
        public void UnregisterChatCommand(string command, Action<string> callback)
        {
            if (ChatCommands.ContainsKey(command))
            {
                ChatCommands.Remove(command);
            }
        }

        /// <summary>
        /// Sends a command packet across the network
        /// </summary>
        /// <param name="commandString">The command word and any arguments delimidated with spaces</param>
        /// <param name="message">Text to be writen in chat</param>
        /// <param name="data">A serialized object used to send game information</param>
        /// <param name="steamId">A players steam id</param>
        public abstract void SendCommand(string commandString, string message = null, byte[] data = null, ulong steamId = ulong.MinValue, bool isReliable = true);

        /// <summary>
        /// Unregisters listeners
        /// </summary>
        public void Close()
        {
            MyLog.Default.Info($"[NetworkAPI] Unregistering communication stream: {ComId}");
            if (UsingTextCommands)
            {
                MyAPIGateway.Utilities.MessageEntered -= HandleChatInput;
            }
            

            MyAPIGateway.Multiplayer.UnregisterMessageHandler(ComId, HandleIncomingPacket);

        }

        /// <summary>
        /// Calls Instance.Close()
        /// </summary>
        public static void Dispose()
        {
            if (IsInitialized)
            {
                Instance.Close();
            }
        }

        /// <summary>
        /// Initializes the default instance of the NetworkAPI
        /// </summary>
        public static void Init(ushort comId, string modName, string keyword = null)
        {
            if (IsInitialized) return;

            if (GetNetworkType() == NetworkTypes.Client)
            {
                Instance = new Client(comId, modName, keyword);
            }
            else
            {
                Instance = new Server(comId, modName, keyword);
            }
        }

        /// <summary>
        /// Finds the type of network system the current instance is running on
        /// </summary>
        /// <returns>MultiplayerTypes Enum</returns>
        public static NetworkTypes GetNetworkType()
        {
            if (!MyAPIGateway.Multiplayer.IsServer)
            {
                //MyLog.Default.Info($"[NetworkAPI] Unregistering communication stream: is client");
                return NetworkTypes.Client;
            }
            else if (MyAPIGateway.Utilities.IsDedicated)
            {
                //MyLog.Default.Info($"[NetworkAPI] Unregistering communication stream: is dedicated");
                return NetworkTypes.Dedicated;
            }
            //MyLog.Default.Info($"[NetworkAPI] Unregistering communication stream: is server");
            return NetworkTypes.Server;
        }
    }
}
