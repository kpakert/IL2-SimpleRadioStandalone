﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.ServiceModel.Channels;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;
using System.Threading;
using System.Windows;
using Ciribob.IL2.SimpleRadio.Standalone.Client.Network.IL2.Models;
using Ciribob.IL2.SimpleRadio.Standalone.Client.Network.Models;
using Ciribob.IL2.SimpleRadio.Standalone.Client.Settings;
using Ciribob.IL2.SimpleRadio.Standalone.Client.Singletons;
using Ciribob.IL2.SimpleRadio.Standalone.Client.Utils;
using Ciribob.IL2.SimpleRadio.Standalone.Common;
using Ciribob.IL2.SimpleRadio.Standalone.Common.Helpers;
using Ciribob.IL2.SimpleRadio.Standalone.Common.Setting;
using Easy.MessageHub;

namespace Ciribob.IL2.SimpleRadio.Standalone.Client.Network.IL2
{
    public class IL2RadioSyncHandler
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly ClientStateSingleton _clientStateSingleton = ClientStateSingleton.Instance;
        private readonly SyncedServerSettings _serverSettings = SyncedServerSettings.Instance;
        private readonly ConnectedClientsSingleton _clients = ConnectedClientsSingleton.Instance;

        private static readonly int RADIO_UPDATE_PING_INTERVAL = 30; //send update regardless of change every X seconds

        private UdpClient _il2UdpListener;
        private UdpClient _il2RadioUpdateSender;

        private readonly GlobalSettingsStore _globalSettings = GlobalSettingsStore.Instance;

        private volatile bool _stop = false;
        public IL2RadioSyncHandler()
        {
        }

        public void Start()
        {
            //reset last sent
            _clientStateSingleton.LastSent = 0;

            Task.Factory.StartNew(() =>
            {
                while (!_stop)
                {
                    var localEp = new IPEndPoint(IPAddress.Any, _globalSettings.GetNetworkSetting(GlobalSettingsKeys.IL2IncomingUDP));
                    try
                    {
                        _il2UdpListener = new UdpClient(localEp);
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, $"Unable to bind to the IL2 Export Listener Socket Port: {localEp.Port}");
                        Thread.Sleep(500);
                    }
                }

                while (!_stop)
                {
                    try
                    {
                        var groupEp = new IPEndPoint(IPAddress.Any,0);
                        var bytes = _il2UdpListener.Receive(ref groupEp);

                        if (bytes.Length > 2)
                        {
                            var messages = IL2UDPMessage.Process(bytes);
                            foreach (var msg in messages)
                            {
                                Logger.Debug($"Recevied Message from IL2 {msg.ToString()}");
                                ProcessUDPMessage(msg);
                            }
                           
                           
                        }
                    }
                    catch (SocketException e)
                    {
                        // SocketException is raised when closing app/disconnecting, ignore so we don't log "irrelevant" exceptions
                        if (!_stop)
                        {
                            Logger.Error(e, "SocketException Handling IL2 Message");
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e, "Exception Handling IL2 Message");
                    }
                }

                try
                {
                    _il2UdpListener.Close();
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Exception stoping IL2 listener ");
                }
                
            });
        }

        public void ProcessUDPMessage(IL2UDPMessage message)
        {
            _clientStateSingleton.IL2ExportLastReceived = DateTime.Now.Ticks;

            bool update = false;

            var playerRadioInfo = _clientStateSingleton.PlayerGameState;

            //copy and compare to look for changes

            /**
             * So ParentID - is an ID of another Client, who owns the vehicle which crew you're participating in now. For instance:
Alan is riding his tank on the server. His ClientID is 12345. His ParentID=-1.
Now Peter is joining Alan's crew as a gunner. He has his own ClientID=54321. But also he has ParentID=12345. 
Donald now joined Alan's crew too as radist. Hi has his own ClientID=34251. But also he has ParentID=12345.
So if someone on Server has ParentID!=-1 but ParentID=12345 - this means that intercom channel started. And the commander of this intercom is another client with  ClientID=12345.
             */

            if (message is ClientDataMessage dataMessage)
            {   //Just set the Client

                update = playerRadioInfo.unitId != dataMessage.ClientID ||
                         !dataMessage.PlayerName.Equals(_clientStateSingleton.LastSeenName);

                playerRadioInfo.unitId = dataMessage.ClientID;

                _clientStateSingleton.LastSeenName = dataMessage.PlayerName;
            }
            else if (message is ControlDataMessage controlDataMessage)
            {
                update =playerRadioInfo.vehicleId!=controlDataMessage.ParentVehicleClientID || playerRadioInfo.coalition != controlDataMessage.Coalition;

                playerRadioInfo.vehicleId = controlDataMessage.ParentVehicleClientID;
                playerRadioInfo.coalition = controlDataMessage.Coalition;
            }
            else if (message is SRSAddressMessage srs)
            {
                if (srs.SRSAddress.Length > 0)
                {
                    //call on main
                    Application.Current.Dispatcher.Invoke(() => { MessageHub.Instance.Publish(srs); });
                }
                
            }

            var diff = new TimeSpan(DateTime.Now.Ticks - _clientStateSingleton.LastSent);

            if (update
                || _clientStateSingleton.LastSent < 1
                || diff.TotalSeconds > RADIO_UPDATE_PING_INTERVAL)
            {
                Logger.Debug("Sending Radio Info To Server - Update");
                _clientStateSingleton.LastSent = DateTime.Now.Ticks;

                MessageHub.Instance.Publish(new PlayerStateUpdate());
            }
        }


        //send updated radio info back to IL2 for ingame GUI
        private void SendRadioUpdateToIL2()
        {
            if (_il2RadioUpdateSender == null)
            {
                _il2RadioUpdateSender = new UdpClient();
            }

            try
            {
                var connectedClientsSingleton = ConnectedClientsSingleton.Instance;
                int[] tunedClients = new int[11];

                if (_clientStateSingleton.IsConnected
                    && _clientStateSingleton.PlayerGameState !=null
                    )
                {

                    for (int i = 0; i < tunedClients.Length; i++)
                    {
                        var clientRadio = _clientStateSingleton.PlayerGameState.radios[i];
                        
                        if (clientRadio.modulation != RadioInformation.Modulation.DISABLED)
                        {
                            tunedClients[i] = connectedClientsSingleton.ClientsOnFreq(clientRadio.freq, clientRadio.modulation);
                        }
                    }
                }
                
                //get currently transmitting or receiving
                var combinedState = new CombinedRadioState()
                {
                    GameState = _clientStateSingleton.PlayerGameState,
                    RadioSendingState = _clientStateSingleton.RadioSendingState,
                    RadioReceivingState = _clientStateSingleton.RadioReceivingState,
                    ClientCountConnected = _clients.Total,
                    TunedClients = tunedClients,
                };

                var message = JsonConvert.SerializeObject(combinedState, new JsonSerializerSettings
                {
                 //   NullValueHandling = NullValueHandling.Ignore,
                    ContractResolver = new JsonIL2PropertiesResolver(),
                }) + "\n";

                var byteData =
                    Encoding.UTF8.GetBytes(message);

                //Logger.Info("Sending Update over UDP 7080 IL2 - 7082 Flight Panels: \n"+message);

                _il2RadioUpdateSender.Send(byteData, byteData.Length,
                    new IPEndPoint(IPAddress.Parse("127.0.0.1"),
                        _globalSettings.GetNetworkSetting(GlobalSettingsKeys.OutgoingIL2UDPInfo))); //send to IL2
            }
            catch (Exception e)
            {
                Logger.Error(e, "Exception Sending IL2 Radio Update Message");
            }
        }


        public void Stop()
        {
            _stop = true;
            try
            {
                _il2UdpListener?.Close();
            }
            catch (Exception ex)
            {
            }

            try
            {
                _il2RadioUpdateSender?.Close();
            }
            catch (Exception ex)
            {
            }
        }
    }
}