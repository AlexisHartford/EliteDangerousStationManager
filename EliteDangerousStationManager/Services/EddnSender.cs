using System;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace EliteDangerousStationManager.Services
{
    public class EddnSender
    {
        private const string EddnRelay = "tcp://eddn.edcd.io:9500";

        public void SendJournalEvent(string commanderName, JObject journalEvent)
        {
            try
            {
                var header = new JObject
                {
                    ["uploaderID"] = commanderName,
                    ["softwareName"] = "EDStationManager",
                    ["softwareVersion"] = "1.0"
                };

                var message = new JObject
                {
                    ["$schemaRef"] = "https://eddn.edcd.io/schemas/journal/1",
                    ["header"] = header,
                    ["message"] = journalEvent
                };

                using var pubSocket = new PublisherSocket();
                pubSocket.Connect(EddnRelay);
                pubSocket.SendFrame(message.ToString(Formatting.None));

                Console.WriteLine("EDDN message sent successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to send EDDN message: " + ex.Message);
            }
        }
    }
}