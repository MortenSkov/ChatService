using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ChatService
{
    public class Program
    {
        static readonly string[] lowerCaseLetters = new string[26] {
            "a", "b", "c", "d", "e", "f", "g", "h", "i", "j",
            "k", "l", "m", "n", "o", "p", "q", "r", "s", "t",
            "u", "v", "w", "x", "y", "z" };

        static readonly string[] upperCaseLetters = new string[26] {
            "A", "B", "C", "D", "E", "F", "G", "H", "I", "J",
            "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T",
            "U", "V", "W", "X", "Y", "Z" };

        static List<string[]> upperAndLowerCollections;

        static HashSet<string> routingKeyCombinations = new();

        // SESSION SETUP
        private static ConnectionFactory? sessionConsumerCf;
        private static ConnectionFactory? sessionPublishCf;

        private static IConnection? sessionConsumerConnection;

        private static IModel? sessionConsumerChannel;

        private static EventingBasicConsumer? sessionConsumer;
        private static string? sessionQueueName;

        private static readonly string sessionRoutingKey_RECEIVE_FROM_GAMESERVER = "session_connection_sendto_key";

        // PLAYER CONNECT

        private static IConnection? playerConsumerConnection;
        private static IConnection? playerPublishConnection;

        private static IModel? playerConsumerChannel;
        private static IModel? playerPublishChannel;

        private static readonly string routingKey_SEND_TO_PLAYER = "player_receivefrom_chatservice";

        private static Dictionary<string[], string> onlinePlayerSessions = new();
        private static Dictionary<string, string> playerPairsInSession = new();

        public static void Main(string[] args)
        {
            upperAndLowerCollections = new() { lowerCaseLetters, upperCaseLetters };

            PlayerConnection_Publisher();
            PlayerConnection_Consumer();

            GameServerSession_Consumer();

            Console.ReadKey();
        }

        public static string CombineNewRoutingKey()
        {
            string finalLetterCombination = "";
            char[] totalLetters = new char[7];
            Random rnd = new Random();
            int combinationLength = 7;

            for (int i = 0; i < combinationLength; i++)
            {
                int upperOrLower = rnd.Next(0, 2); // either 1 or 2
                string[] tmp = upperAndLowerCollections[upperOrLower];
                int alphabetPlacement = rnd.Next(0, tmp.Length);
                char charLetter = char.Parse(tmp[alphabetPlacement]);
                totalLetters[i] = charLetter;

                Console.WriteLine(charLetter.ToString());
            }

            finalLetterCombination = new string(totalLetters);

            if (routingKeyCombinations.Contains(finalLetterCombination))
            {
                bool routingKeyExists = true;

                while (routingKeyExists)
                {
                    for (int i = 0; i < combinationLength; i++)
                    {
                        int upperOrLower = rnd.Next(0, 2); // either 1 or 2
                        string[] tmp = upperAndLowerCollections[upperOrLower];
                        int alphabetPlacement = rnd.Next(0, tmp.Length);
                        char charLetter = char.Parse(tmp[alphabetPlacement]);
                        totalLetters[i] = charLetter;
                    }

                    finalLetterCombination = new string(totalLetters.ToString());

                    if (!routingKeyCombinations.Contains(finalLetterCombination))
                    {
                        routingKeyExists = false;
                    }
                }
            }

            routingKeyCombinations.Add(finalLetterCombination);

            Console.WriteLine("");

            return finalLetterCombination;
        }

        // GAME SERVER CONNECTION BELOW

        public static void GameServerSession_Consumer()
        {
            sessionConsumerConnection = sessionConsumerCf.CreateConnection();
            sessionConsumerChannel = sessionConsumerConnection.CreateModel();

            sessionConsumerChannel.ExchangeDeclare(exchange: "direct_session_connect", type: ExchangeType.Direct, autoDelete: true);
            sessionQueueName = sessionConsumerChannel.QueueDeclare().QueueName;
            sessionConsumerChannel.QueueBind(queue: sessionQueueName, exchange: "direct_session_connect", routingKey: sessionRoutingKey_RECEIVE_FROM_GAMESERVER);

            sessionConsumer = new EventingBasicConsumer(sessionConsumerChannel);

            sessionConsumer.Received += (model, ea) =>
            {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);

                Console.WriteLine("Received message from Session Queue: {0}", message);

                string[] splitMessage = message.Split('_');

                string newSessionRoutingKey = CombineNewRoutingKey();

                onlinePlayerSessions.Add(splitMessage, newSessionRoutingKey);
                playerPairsInSession.Add(splitMessage[0], splitMessage[1]);

                StartNewSessionConnection(splitMessage, newSessionRoutingKey);
            };

            sessionConsumerChannel.BasicConsume(queue: sessionQueueName,
                autoAck: true,
                consumer: sessionConsumer);
        }

        public static void StartNewSessionConnection(string[] players, string routingKey)
        {
            var tmpCf = new ConnectionFactory() { HostName = "localhost" };

            using var tmpPlayerConnection = tmpCf.CreateConnection();
            using var tmpPlayerChannel = tmpPlayerConnection.CreateModel();

            foreach (var player in players)
            {
                Console.WriteLine("Sending new routing key to player: {0}", player);

                tmpPlayerChannel.ExchangeDeclare(exchange: player, type: ExchangeType.Direct, autoDelete: true);

                var body = Encoding.UTF8.GetBytes(routingKey);

                tmpPlayerChannel.BasicPublish(exchange: player,
                    routingKey: routingKey_SEND_TO_PLAYER,
                    basicProperties: null,
                    body: body);

                UniquePlayerSession_Publisher(player);
                UniquePlayerSession_Consumer(player, routingKey);
            }
        }

        public static void UniquePlayerSession_Publisher(string playerName)
        {
            playerPublishChannel.ExchangeDeclare(exchange: playerName, type: ExchangeType.Direct, autoDelete: true);
        }

        public static void UniquePlayerSession_Consumer(string playerName, string routingKey)
        {
            playerConsumerChannel.ExchangeDeclare(exchange: playerName, ExchangeType.Direct, autoDelete: true);
            string tmpQueueName = playerConsumerChannel.QueueDeclare().QueueName;
            playerConsumerChannel.QueueBind(queue: tmpQueueName, exchange: playerName, routingKey: routingKey);

            var tmpConsumerEvent = new EventingBasicConsumer(playerConsumerChannel);

            tmpConsumerEvent.Received += (model, ea) =>
            {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);

                Console.WriteLine("Player '{0}': {1}", playerName, message);

                SendMessageToOtherOpponent(message, playerName, routingKey);
            };

            playerConsumerChannel.BasicConsume(queue: tmpQueueName,
                autoAck: true,
                consumer: tmpConsumerEvent);
        }

        public static void SendMessageToOtherOpponent(string msgToSent, string playerWhoSent, string routingKey)
        {
            string playerToReceive = "";

            foreach (KeyValuePair<string, string> entry in playerPairsInSession)
            {
                if (entry.Key == playerWhoSent)
                {
                    playerToReceive = entry.Value;
                }
                else if (entry.Value == playerWhoSent)
                {
                    playerToReceive = entry.Key;
                }
                break;
            }

            string opponentRoutingKey = routingKey + "_" + playerToReceive;

            string newMsg = playerWhoSent + ": " + msgToSent;

            var body = Encoding.UTF8.GetBytes(newMsg);

            playerPublishChannel.BasicPublish(exchange: playerToReceive,
                routingKey: opponentRoutingKey,
                basicProperties: null,
                body: body);

        }

        public static void PlayerConnection_Publisher()
        {
            sessionPublishCf = new ConnectionFactory() { HostName = "localhost" };
            sessionPublishCf.RequestedHeartbeat = TimeSpan.FromSeconds(10);

            playerPublishConnection = sessionPublishCf.CreateConnection();
            playerPublishChannel = playerPublishConnection.CreateModel();

            playerPublishChannel.ExchangeDeclare(exchange: "player_chatservice_connect", type: ExchangeType.Direct, autoDelete: true);
        }

        public static void PlayerConnection_Consumer()
        {
            sessionConsumerCf = new ConnectionFactory() { HostName = "localhost" };
            sessionConsumerCf.RequestedHeartbeat = TimeSpan.FromSeconds(10);

            playerConsumerConnection = sessionConsumerCf.CreateConnection();
            playerConsumerChannel = playerConsumerConnection.CreateModel();
        }

    }
}
