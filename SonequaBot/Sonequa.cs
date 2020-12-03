using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SonequaBot.Commands;
using SonequaBot.Commands.Interfaces;
using SonequaBot.Commands.Interfaces.Responses;
using SonequaBot.Models;
using SonequaBot.Services;
using SonequaBot.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwitchLib.Api;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;

namespace SonequaBot
{
    public class Sonequa : BackgroundService
    {
        private readonly ILogger<Sonequa> _logger;

        readonly ConnectionCredentials connectionCredentials;
        private readonly TwitchClient client = new TwitchClient();
        readonly TwitchAPI twitchAPI = new TwitchAPI();

        private readonly string[] BotUsers = new string[] { "sonequabot", "streamelements" };

        Dictionary<string, ConnectedUser> ConnectedUsers = new Dictionary<string, ConnectedUser>();

        List<ICommand> BotCommands = new List<ICommand>();
        private readonly HubConnection connection;

        private readonly SonequaSettings _options;

        private readonly SentimentAnalysisService _sentimentAnalysisService;
        private List<SentimentScores> sentimentScores = new List<SentimentScores>();
        private SentimentScores currentChatSentiment = new SentimentScores();

        public Sonequa(ILogger<Sonequa> logger, SonequaSettings options, SentimentAnalysisService sentimentAnalysisService)
        {
            _logger = logger;
            _options = options;
            _sentimentAnalysisService = sentimentAnalysisService;

            connectionCredentials = new ConnectionCredentials(_options.BotUsername, _options.BotToken);

            connection = new HubConnectionBuilder()
                .WithUrl(_options.SonequaWebUrl)
                .Build();

            connection.Closed += async (error) =>
            {
                await Task.Delay(new Random().Next(0, 5) * 1000);
                await connection.StartAsync();
            };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Connect();

            try
            {
                await connection.StartAsync();
                _logger.LogInformation("Connection started");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
            }
        }

        internal void Connect()
        {
            _logger.LogInformation("Connecting...");

            twitchAPI.Settings.ClientId = _options.ClientId;

            InitializeBotCommands();
            InitializeBot();
        }

        private void InitializeBotCommands()
        {
            //BotCommands.Add(new CommandHi()); Not needed in twitch
            BotCommands.Add(new CommandJava()); ;
            BotCommands.Add(new CommandPhp());
            BotCommands.Add(new CommandDevastante());
            BotCommands.Add(new CommandSlap());
            BotCommands.Add(new CommandDiceRoll());
            BotCommands.Add(new CommandFriday());
            BotCommands.Add(new CommandDisagio());
            BotCommands.Add(new CommandGren());
            BotCommands.Add(new CommandDebug());
            BotCommands.Add(new CommandDio());
            BotCommands.Add(new CommandPaura());
            BotCommands.Add(new CommandKasu());
            BotCommands.Add(new CommandMerda());
            BotCommands.Add(new CommandAnsia());
            BotCommands.Add(new CommandAccompagnare());
            BotCommands.Add(new CommandZinghero());
        }

        private void InitializeBot()
        {
            client.Initialize(connectionCredentials, _options.ChannelName);
            client.Connect();

            client.OnUserJoined += Client_OnUserJoined;
            client.OnUserLeft += Client_OnUserLeft;

            client.OnConnected += Client_OnConnected;
            client.OnMessageReceived += Client_OnMessageReceived;
        }

        private void Client_OnUserLeft(object sender, TwitchLib.Client.Events.OnUserLeftArgs e)
        {
            ConnectedUsers.Remove(e.Username);

            _logger.LogWarning($"The user left: {e.Username}");
            _logger.LogWarning($"Total user on channel: {ConnectedUsers.Count}");
        }

        private async void Client_OnUserJoined(object sender, TwitchLib.Client.Events.OnUserJoinedArgs e)
        {
            ConnectedUsers.Add(e.Username, new ConnectedUser(e.Username));

            await connection.SendAsync("SendTask", "SendUserAppear", e.Username);

            _logger.LogWarning($"New user on channel: {e.Username}");
            _logger.LogWarning($"Total user on channel: {ConnectedUsers.Count}");
        }

        private void Client_OnConnected(object sender, TwitchLib.Client.Events.OnConnectedArgs e)
        {
            client.SendMessage(_options.ChannelName, $"Hi to everyone. I am Sonequabot and I am alive. Again.");
        }


        private async void Client_OnMessageReceived(object sender, TwitchLib.Client.Events.OnMessageReceivedArgs e)
        {
            String invoker = e.ChatMessage.Username;

            try
            {
                foreach (var command in BotCommands)
                {
                    if (command.IsActivated(e.ChatMessage.Message))
                    {
                        switch (true)
                        {
                            case true when command is IResponseMessage commandMessage:
                                client.SendMessage(_options.ChannelName, commandMessage.GetMessage(e));
                                break;

                            case true when command is IResponseVisual commandVisual:
                                await connection.SendAsync("SendTask", commandVisual.GetVisualEvent(e), "");
                                break;
                        }

                        return; // if activated exit, if not multiple sentiment of !devastante will be UBER negative 
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                client.SendWhisper(invoker, ex.Message);
            }

            await ProcessSentiment(e);
        }

        private async Task ProcessSentiment(OnMessageReceivedArgs e)
        {
            // to remove noise 
            if (e.ChatMessage.Message.Length < 10)
            {
                return;
            }

            // limit number of stored SentimentScores to react quickly to changes 
            if (sentimentScores.Count > 10)
            {
                sentimentScores.RemoveAt(0);
            }

            var currentScore = _sentimentAnalysisService.ElaborateSentence(e.ChatMessage.Message);

            await connection.SendAsync("SendTask", "SendSentiment", currentScore.GetSentiment().ToString().ToLower());

            var currentUnrankedSentiment = new Dictionary<SentimentScores.TextSentiment, double>
            {
                {SentimentScores.TextSentiment.Positive, currentScore.Positive},
                {SentimentScores.TextSentiment.Neutral, currentScore.Neutral},
                {SentimentScores.TextSentiment.Negative, currentScore.Negative},
            };

            var currentRankedSentiment = currentUnrankedSentiment.OrderBy(item => item.Value);

            _logger.LogInformation(string.Concat(
                    "currentScore:",
                    Environment.NewLine,
                    string.Join(
                        Environment.NewLine,
                        currentRankedSentiment.Select(a => $"{a.Key}: {a.Value}")
                    )
                )
            );

            var processedSentiment = new SentimentScores();
            switch (currentRankedSentiment.Last().Key)
            {
                case SentimentScores.TextSentiment.Positive:
                    processedSentiment.Positive = currentRankedSentiment.Last().Value;
                    break;
                case SentimentScores.TextSentiment.Neutral:
                    processedSentiment.Neutral = currentRankedSentiment.Last().Value;
                    break;
                case SentimentScores.TextSentiment.Negative:
                    processedSentiment.Negative = currentRankedSentiment.Last().Value;
                    break;
            }

            sentimentScores.Add(processedSentiment);

            var chatUnrankedSentiment = new Dictionary<SentimentScores.TextSentiment, double>
            {
                {SentimentScores.TextSentiment.Positive, sentimentScores.Average(c => c.Positive)},
                {SentimentScores.TextSentiment.Neutral, sentimentScores.Average(c => c.Neutral)},
                {SentimentScores.TextSentiment.Negative, sentimentScores.Average(c => c.Negative)},
            };

            var chatRankedSentiment = chatUnrankedSentiment.OrderBy(item => item.Value);

            // set current chat sentiment with values
            currentChatSentiment.SetSentiment(chatRankedSentiment.Last().Key);
            currentChatSentiment.Positive = chatUnrankedSentiment[SentimentScores.TextSentiment.Positive];
            currentChatSentiment.Neutral = chatUnrankedSentiment[SentimentScores.TextSentiment.Neutral];
            currentChatSentiment.Negative = chatUnrankedSentiment[SentimentScores.TextSentiment.Negative];

            _logger.LogInformation(string.Concat(
                    "Chat sentiment:",
                    Environment.NewLine,
                    string.Join(
                        Environment.NewLine,
                        chatRankedSentiment.Select(a => $"{a.Key}: {a.Value}")
                    )
                )
            );

            // interesting can be used with a gauge or a vertical meter that can go from -1 to 1
            double absoluteSentiment = (currentChatSentiment.Positive - currentChatSentiment.Neutral) -
                                       (currentChatSentiment.Negative - currentChatSentiment.Neutral);
            _logger.LogInformation(string.Concat("(", sentimentScores.Count.ToString(), ")", " - Absolute sentiment:", absoluteSentiment));

            await connection.SendAsync("SendTask", "SendGaugeSentiment", absoluteSentiment);
        }
    }
}
