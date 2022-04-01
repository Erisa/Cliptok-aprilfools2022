using Cliptok.Helpers;
using Cliptok.Modules;
using DSharpPlus;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Exceptions;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.SlashCommands;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Serilog;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using static Cliptok.Helpers.ShellCommand;

namespace Cliptok
{
    // https://stackoverflow.com/a/34944872
    public class OutputCapture : TextWriter, IDisposable
    {
        private TextWriter stdOutWriter;
        public TextWriter Captured { get; private set; }
        public override Encoding Encoding { get { return Encoding.ASCII; } }

        public OutputCapture()
        {
            stdOutWriter = Console.Out;
            Console.SetOut(this);
            Captured = new StringWriter();
        }

        override public void Write(string output)
        {
            // Capture the output and also send it to StdOut
            Captured.Write(output);
            stdOutWriter.Write(output);
        }

        override public void WriteLine(string output)
        {
            // Capture the output and also send it to StdOut
            Captured.WriteLine(output);
            stdOutWriter.WriteLine(output);
        }
    }
    public class AvatarResponseBody
    {
        [JsonProperty("matched")]
        public bool Matched { get; set; }

        [JsonProperty("key")]
        public string Key { get; set; }
    }

    class Program : BaseCommandModule
    {
        public static DiscordClient discord;
        static CommandsNextExtension commands;
        public static Random rnd = new();
        public static ConfigJson cfgjson;
        public static ConnectionMultiplexer redis;
        public static IDatabase db;
        internal static EventId CliptokEventID { get; } = new EventId(1000, "Cliptok");

        public static string[] avatars;

        public static string[] badUsernames;
        public static List<ulong> autoBannedUsersCache = new();
        public static DiscordChannel logChannel;
        public static DiscordChannel userLogChannel;
        public static DiscordChannel badMsgLog;
        public static DiscordGuild homeGuild;

        public static Random rand = new Random();
        public static HasteBinClient hasteUploader;

        public static OutputCapture outputCapture;

        static public readonly HttpClient httpClient = new();

        static public string[] waterList;
        static public List<string> allWordList;

        static public int maxWordCount;
        static public string winningWord;

        public static void UpdateLists()
        {
            foreach (var list in cfgjson.WordListList)
            {
                var listOutput = File.ReadAllLines($"Lists/{list.Name}");
                cfgjson.WordListList[cfgjson.WordListList.FindIndex(a => a.Name == list.Name)].Words = listOutput;
            }

            waterList = File.ReadAllLines("Lists/water.txt");
        }

        public static async Task<bool> CheckAndDehoistMemberAsync(DiscordMember targetMember)
        {

            if (
                !(
                    targetMember.DisplayName[0] != ModCmds.dehoistCharacter
                    && (
                        cfgjson.AutoDehoistCharacters.Contains(targetMember.DisplayName[0])
                        || (targetMember.Nickname != null && targetMember.Nickname[0] != targetMember.Username[0] && cfgjson.SecondaryAutoDehoistCharacters.Contains(targetMember.Nickname[0]))
                        )
                ))
            {
                return false;
            }

            try
            {
                await targetMember.ModifyAsync(a =>
                {
                    a.Nickname = ModCmds.DehoistName(targetMember.DisplayName);
                    a.AuditLogReason = "Dehoisted";
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        static void Main(string[] args)
        {
            using (outputCapture = new OutputCapture())
            {
                MainAsync(args).ConfigureAwait(false).GetAwaiter().GetResult();
            }
        }

        static async Task MainAsync(string[] _)
        {
            // load all words into memory
            var allWords = File.ReadAllLines(@"Lists/words.txt");
            allWordList = new List<string>(allWords);
            if (allWordList == null)
            {
                allWordList.Add("torchsucks");
            }

            winningWord = "moisture"; // word that does something different idk a checkmark reaction? not even sure if it'll be used
            maxWordCount = 25; // maximum number of words per message to prevent people from breaking cliptok

            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var logFormat = "[{Timestamp:yyyy-MM-dd HH:mm:ss zzz}] [{Level}] {Message}{NewLine}{Exception}\n";
            Log.Logger = new LoggerConfiguration()
#if DEBUG
                .MinimumLevel.Debug()
#else
                .Filter.ByExcluding("Contains(@m, 'Unknown event:')")
                .MinimumLevel.Information()
#endif
                .WriteTo.Console(outputTemplate: logFormat)
                .CreateLogger();

            var logFactory = new LoggerFactory().AddSerilog();

            string token;
            var json = "";

            string configFile = "config.json";
#if DEBUG
            configFile = "config.dev.json";
#endif

            using (var fs = File.OpenRead(configFile))
            using (var sr = new StreamReader(fs, new UTF8Encoding(false)))
                json = await sr.ReadToEndAsync();

            cfgjson = JsonConvert.DeserializeObject<ConfigJson>(json);

            hasteUploader = new HasteBinClient(cfgjson.HastebinEndpoint);

            UpdateLists();

            if (File.Exists("Lists/usernames.txt"))
                badUsernames = File.ReadAllLines("Lists/usernames.txt");
            else
                badUsernames = new string[0];

            avatars = File.ReadAllLines("Lists/avatars.txt");

            if (Environment.GetEnvironmentVariable("CLIPTOK_TOKEN") != null)
                token = Environment.GetEnvironmentVariable("CLIPTOK_TOKEN");
            else
                token = cfgjson.Core.Token;

            if (Environment.GetEnvironmentVariable("REDIS_URL") != null)
                redis = ConnectionMultiplexer.Connect(Environment.GetEnvironmentVariable("REDIS_URL"));
            else
            {
                string redisHost;
                if (Environment.GetEnvironmentVariable("REDIS_DOCKER_OVERRIDE") != null)
                    redisHost = "redis";
                else
                    redisHost = cfgjson.Redis.Host;
                redis = ConnectionMultiplexer.Connect($"{redisHost}:{cfgjson.Redis.Port}");
            }

            db = redis.GetDatabase();

            // Migration away from a broken attempt at a key in the past.
            db.KeyDelete("messages");

            discord = new DiscordClient(new DiscordConfiguration
            {
                Token = token,
                TokenType = TokenType.Bot,
#if DEBUG
                MinimumLogLevel = LogLevel.Debug,
#else
                MinimumLogLevel = LogLevel.Information,
#endif
                LoggerFactory = logFactory,
                Intents = DiscordIntents.All
            });

            if (Environment.GetEnvironmentVariable("CLIPTOK_GITHUB_TOKEN") == null || Environment.GetEnvironmentVariable("CLIPTOK_GITHUB_TOKEN") == "githubtokenhere")
                discord.Logger.LogWarning(CliptokEventID, "GitHub API features disabled due to missing access token.");

            if (Environment.GetEnvironmentVariable("RAVY_API_TOKEN") == null || Environment.GetEnvironmentVariable("RAVY_API_TOKEN") == "goodluckfindingone")
                discord.Logger.LogWarning(CliptokEventID, "Ravy API features disabled due to missing API token.");


            var slash = discord.UseSlashCommands();
            slash.SlashCommandErrored += async (s, e) =>
            {
                if (e.Exception is SlashExecutionChecksFailedException slex)
                {
                    foreach (var check in slex.FailedChecks)
                        if (check is SlashRequireHomeserverPermAttribute att && e.Context.CommandName != "edit")
                        {
                            var level = Warnings.GetPermLevel(e.Context.Member);
                            var levelText = level.ToString();
                            if (level == ServerPermLevel.Nothing && rand.Next(1, 100) == 69)
                                levelText = $"naught but a thing, my dear human. Congratulations, you win {Program.rand.Next(1, 10)} bonus points.";

                            await e.Context.CreateResponseAsync(
                                InteractionResponseType.ChannelMessageWithSource,
                                new DiscordInteractionResponseBuilder().WithContent(
                                    $"{cfgjson.Emoji.NoPermissions} Invalid permission level to use command **{e.Context.CommandName}**!\n" +
                                    $"Required: `{att.TargetLvl}`\n" +
                                    $"You have: `{Warnings.GetPermLevel(e.Context.Member)}`")
                                    .AsEphemeral(true)
                                );
                        }
                }
            };

            Task ClientError(DiscordClient client, ClientErrorEventArgs e)
            {
                client.Logger.LogError(CliptokEventID, e.Exception, "Client threw an exception");
                return Task.CompletedTask;
            }

            slash.RegisterCommands<SlashCommands>(cfgjson.ServerID);

            async Task OnReaction(DiscordClient client, MessageReactionAddEventArgs e)
            {
                Task.Run(async () =>
                {
                    if (e.Emoji.Id != cfgjson.HeartosoftId || e.Channel.IsPrivate || e.Guild.Id != cfgjson.ServerID)
                        return;

                    bool handled = false;

                    DiscordMessage targetMessage = await e.Channel.GetMessageAsync(e.Message.Id);

                    DiscordEmoji noHeartosoft = await e.Guild.GetEmojiAsync(cfgjson.NoHeartosoftId);

                    await Task.Delay(1000);

                    if (targetMessage.Author.Id == e.User.Id)
                    {
                        await targetMessage.DeleteReactionAsync(e.Emoji, e.User);
                        handled = true;
                    }

                    foreach (string word in cfgjson.RestrictedHeartosoftPhrases)
                    {
                        if (targetMessage.Content.ToLower().Contains(word))
                        {
                            if (!handled)
                                await targetMessage.DeleteReactionAsync(e.Emoji, e.User);

                            await targetMessage.CreateReactionAsync(noHeartosoft);
                            return;
                        }
                    }
                });
            }

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            async Task OnReady(DiscordClient client, ReadyEventArgs e)
            {
                Task.Run(async () =>
                {
                    client.Logger.LogInformation(CliptokEventID, $"Logged in as {client.CurrentUser.Username}#{client.CurrentUser.Discriminator}");
                    logChannel = await discord.GetChannelAsync(cfgjson.LogChannel);
                    userLogChannel = await discord.GetChannelAsync(cfgjson.UserLogChannel);
                    badMsgLog = await discord.GetChannelAsync(cfgjson.InvestigationsChannelId);
                    homeGuild = await discord.GetGuildAsync(cfgjson.ServerID);

                    Mutes.CheckMutesAsync();
                    ModCmds.CheckBansAsync();
                    ModCmds.CheckRemindersAsync();

                    string commitHash = "";
                    string commitMessage = "";
                    string commitTime = "";

                    if (File.Exists("CommitHash.txt"))
                    {
                        using var sr = new StreamReader("CommitHash.txt");
                        commitHash = sr.ReadToEnd();
                    }

                    if (Environment.GetEnvironmentVariable("RAILWAY_GIT_COMMIT_SHA") != null)
                    {
                        commitHash = Environment.GetEnvironmentVariable("RAILWAY_GIT_COMMIT_SHA");
                        commitHash = commitHash.Substring(0, Math.Min(commitHash.Length, 7));
                    }

                    if (string.IsNullOrWhiteSpace(commitHash))
                    {
                        commitHash = "dev";
                    }

                    if (File.Exists("CommitMessage.txt"))
                    {
                        using var sr = new StreamReader("CommitMessage.txt");
                        commitMessage = sr.ReadToEnd();
                    }

                    if (Environment.GetEnvironmentVariable("RAILWAY_GIT_COMMIT_MESSAGE") != null)
                    {
                        commitMessage = Environment.GetEnvironmentVariable("RAILWAY_GIT_COMMIT_MESSAGE");
                    }

                    if (string.IsNullOrWhiteSpace(commitMessage))
                    {
                        commitMessage = "N/A (Only available when built with Docker)";
                    }

                    if (File.Exists("CommitTime.txt"))
                    {
                        using var sr = new StreamReader("CommitTime.txt");
                        commitTime = sr.ReadToEnd();
                    }

                    if (string.IsNullOrWhiteSpace(commitTime))
                    {
                        commitTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
                    }

                    bool listSuccess = false;
                    if (cfgjson.GitListDirectory != null && cfgjson.GitListDirectory != "")
                    {

                        ShellResult finishedShell = RunShellCommand($"cd Lists/{cfgjson.GitListDirectory} && git pull");

                        string result = Regex.Replace(finishedShell.result, "ghp_[0-9a-zA-Z]{36}", "ghp_REDACTED").Replace(Environment.GetEnvironmentVariable("CLIPTOK_TOKEN"), "REDACTED");

                        if (finishedShell.proc.ExitCode != 0)
                        {
                            listSuccess = false;
                            client.Logger.LogError(eventId: CliptokEventID, $"Error updating lists:\n{result}");
                        }
                        else
                        {
                            UpdateLists();
                            client.Logger.LogInformation(eventId: CliptokEventID, $"Success updating lists:\n{result}");
                            listSuccess = true;
                        }
                    }

                    var cliptokChannel = await client.GetChannelAsync(cfgjson.HomeChannel);
                    cliptokChannel.SendMessageAsync($"{cfgjson.Emoji.Connected} {discord.CurrentUser.Username} connected successfully!\n\n" +
                        $"**Version**: `{commitHash.Trim()}`\n" +
                        $"**Version timestamp**: `{commitTime}`\n**Framework**: `{RuntimeInformation.FrameworkDescription}`\n" +
                        $"**Platform**: `{RuntimeInformation.OSDescription}`\n" +
                        $"**Library**: `DSharpPlus {discord.VersionString}`\n" +
                        $"**List update success**: `{listSuccess}`\n\n" +
                        $"Most recent commit message:\n" +
                        $"```\n" +
                        $"{commitMessage}\n" +
                        $"```");

                });
            }

            async Task<bool> UsernameCheckAsync(DiscordMember member)
            {
                var guild = Program.homeGuild;
                if (db.HashExists("unbanned", member.Id))
                    return false;

                bool result = false;

                foreach (var username in badUsernames)
                {
                    // emergency failsafe, for newlines and other mistaken entries
                    if (username.Length < 4)
                        continue;

                    if (member.Username.ToLower().Contains(username.ToLower()))
                    {
                        if (autoBannedUsersCache.Contains(member.Id))
                            break;
                        IEnumerable<ulong> enumerable = autoBannedUsersCache.Append(member.Id);
                        await Bans.BanFromServerAsync(member.Id, "Automatic ban for matching patterns of common bot accounts. Please appeal if you are a human.", discord.CurrentUser.Id, guild, 7, null, default, true);
                        var embed = new DiscordEmbedBuilder()
                            .WithTimestamp(DateTime.Now)
                            .WithFooter($"User ID: {member.Id}", null)
                            .WithAuthor($"{member.Username}#{member.Discriminator}", null, member.AvatarUrl)
                            .AddField("Infringing name", member.Username)
                            .AddField("Matching pattern", username)
                            .WithColor(new DiscordColor(0xf03916));
                        var investigations = await discord.GetChannelAsync(cfgjson.InvestigationsChannelId);
                        await investigations.SendMessageAsync($"{cfgjson.Emoji.Banned} {member.Mention} was banned for matching blocked username patterns.", embed);
                        result = true;
                        break;
                    }
                }

                return result;
            }

            async Task GuildMemberAdded(DiscordClient client, GuildMemberAddEventArgs e)
            {
                Task.Run(async () =>
                {
                    if (e.Guild.Id != cfgjson.ServerID)
                        return;

                    var builder = new DiscordEmbedBuilder()
                       .WithColor(new DiscordColor(0x3E9D28))
                       .WithTimestamp(DateTimeOffset.Now)
                       .WithThumbnail(e.Member.AvatarUrl)
                       .WithAuthor(
                           name: $"{e.Member.Username}#{e.Member.Discriminator} has joined",
                           iconUrl: e.Member.AvatarUrl
                        )
                       .AddField("User", e.Member.Mention, false)
                       .AddField("User ID", e.Member.Id.ToString(), false)
                       .AddField("Action", "Joined the server", false)
                       .WithFooter($"{client.CurrentUser.Username}JoinEvent");

                    userLogChannel.SendMessageAsync($"{cfgjson.Emoji.UserJoin} **Member joined the server!** - {e.Member.Id}", builder);

                    var joinWatchlist = await Program.db.ListRangeAsync("joinWatchedUsers");

                    if (joinWatchlist.Contains(e.Member.Id))
                    {
                        badMsgLog.SendMessageAsync($"{cfgjson.Emoji.Warning} Watched user {e.Member.Mention} just joined the server!", builder);
                    }

                    if (db.HashExists("raidmode", e.Guild.Id))
                    {
                        try
                        {
                            await e.Member.SendMessageAsync($"Hi, you tried to join **{e.Guild.Name}** while it was in lockdown and your join was refused.\nPlease try to join again later.");
                        }
                        catch (DSharpPlus.Exceptions.UnauthorizedException)
                        {
                            // welp, their DMs are closed. not my problem.
                        }
                        await e.Member.RemoveAsync();
                    }

                    if (await db.HashExistsAsync("mutes", e.Member.Id))
                    {
                        // todo: store per-guild
                        DiscordRole mutedRole = e.Guild.GetRole(cfgjson.MutedRole);
                        await e.Member.GrantRoleAsync(mutedRole, "Reapplying mute on join: possible mute evasion.");
                    }
                    else if (e.Member.CommunicationDisabledUntil != null)
                    {
                        await e.Member.TimeoutAsync(null, "Removing timeout since member was presumably unmuted while left");
                    }
                    CheckAndDehoistMemberAsync(e.Member);

                    if (db.HashExists("unbanned", e.Member.Id))
                        return;

                    if (avatars.Contains(e.Member.AvatarHash))
                    {
                        var _ = Bans.BanSilently(e.Guild, e.Member.Id, "Secret sauce");
                        await badMsgLog.SendMessageAsync($"{cfgjson.Emoji.Banned} Raid-banned {e.Member.Mention} for matching avatar: {e.Member.AvatarUrl.Replace("1024", "128")}");
                    }

                    string banDM = $"You have been automatically banned from **{e.Guild.Name}** for matching patterns of known raiders.\n" +
                            $"Please send an appeal and you will be unbanned as soon as possible: {Program.cfgjson.AppealLink}\n" +
                            $"The requirements for appeal can be ignored in this case. Sorry for any inconvenience caused.";

                    foreach (var IdAutoBanSet in Program.cfgjson.AutoBanIds)
                    {
                        if (db.HashExists(IdAutoBanSet.Name, e.Member.Id))
                        {
                            return;
                        }

                        if (e.Member.Id > IdAutoBanSet.LowerBound && e.Member.Id < IdAutoBanSet.UpperBound)
                        {
                            await e.Member.SendMessageAsync(banDM);

                            await e.Member.BanAsync(7, "Matching patterns of known raiders, please unban if appealed.");

                            await badMsgLog.SendMessageAsync($"{Program.cfgjson.Emoji.Banned} Automatically appeal-banned {e.Member.Mention} for matching the creation date of the {IdAutoBanSet.Name} DM scam raiders.");
                        }

                        Program.db.HashSet(IdAutoBanSet.Name, e.Member.Id, true);
                    }

                });
            }

            async Task GuildMemberRemoved(DiscordClient client, GuildMemberRemoveEventArgs e)
            {
                Task.Run(async () =>
                {
                    if (e.Guild.Id != cfgjson.ServerID)
                        return;

                    var muteRole = e.Guild.GetRole(cfgjson.MutedRole);
                    var userMute = await db.HashGetAsync("mutes", e.Member.Id);

                    if (!userMute.IsNull && !e.Member.Roles.Contains(muteRole))
                        db.HashDeleteAsync("mutes", e.Member.Id);

                    if (e.Member.Roles.Contains(muteRole) && userMute.IsNull)
                    {
                        MemberPunishment newMute = new()
                        {
                            MemberId = e.Member.Id,
                            ModId = discord.CurrentUser.Id,
                            ServerId = e.Guild.Id,
                            ExpireTime = null,
                            ActionTime = DateTime.Now
                        };

                        db.HashSetAsync("mutes", e.Member.Id, JsonConvert.SerializeObject(newMute));
                    }

                    if (!userMute.IsNull && !e.Member.Roles.Contains(muteRole))
                        db.HashDeleteAsync("mutes", e.Member.Id);

                    string rolesStr = "None";

                    if (e.Member.Roles.Any())
                    {
                        rolesStr = "";

                        foreach (DiscordRole role in e.Member.Roles.OrderBy(x => x.Position).Reverse())
                        {
                            rolesStr += role.Mention + " ";
                        }
                    }

                    var builder = new DiscordEmbedBuilder()
                        .WithColor(new DiscordColor(0xBA4119))
                        .WithTimestamp(DateTimeOffset.Now)
                        .WithThumbnail(e.Member.AvatarUrl)
                        .WithAuthor(
                            name: $"{e.Member.Username}#{e.Member.Discriminator} has left",
                            iconUrl: e.Member.AvatarUrl
                         )
                        .AddField("User", e.Member.Mention, false)
                        .AddField("User ID", e.Member.Id.ToString(), false)
                        .AddField("Action", "Left the server", false)
                        .AddField("Roles", rolesStr)
                        .WithFooter($"{client.CurrentUser.Username}LeaveEvent");

                    userLogChannel.SendMessageAsync($"{cfgjson.Emoji.UserLeave} **Member left the server!** - {e.Member.Id}", builder);

                    var joinWatchlist = await Program.db.ListRangeAsync("joinWatchedUsers");

                    if (joinWatchlist.Contains(e.Member.Id))
                    {
                        badMsgLog.SendMessageAsync($"{cfgjson.Emoji.Warning} Watched user {e.Member.Mention} just left the server!", builder);
                    }
                });
            }

            async Task GuildMemberUpdated(DiscordClient client, GuildMemberUpdateEventArgs e)
            {
                Task.Run(async () =>
                {
                    var muteRole = e.Guild.GetRole(cfgjson.MutedRole);
                    var userMute = await db.HashGetAsync("mutes", e.Member.Id);

                    // If they're externally unmuted, untrack it?
                    // But not if they just joined.
                    var currentTime = DateTime.Now;
                    var joinTime = e.Member.JoinedAt.DateTime;
                    var differrence = currentTime.Subtract(joinTime).TotalSeconds;
                    if (differrence > 10 && !userMute.IsNull && !e.Member.Roles.Contains(muteRole))
                        db.HashDeleteAsync("mutes", e.Member.Id);

                    bool usernameBanned = await UsernameCheckAsync(e.Member);
                    if (usernameBanned)
                        return;

                    CheckAndDehoistMemberAsync(e.Member);
                    CheckAvatarsAsync(e.Member);
                }
                );
            }

            async Task<bool> CheckAvatarsAsync(DiscordMember member)
            {
                if (Environment.GetEnvironmentVariable("RAVY_API_TOKEN") == null || Environment.GetEnvironmentVariable("RAVY_API_TOKEN") == "goodluckfindingone")
                    return false;

                string usedHash;
                string usedUrl;

                if (member.GuildAvatarHash == null && member.AvatarHash == null)
                    return false;

                // turns out checking guild avatars isnt important

                //               if (member.GuildAvatarHash != null)
                //               {
                //                   usedHash = member.GuildAvatarHash;
                //                   usedUrl = member.GuildAvatarUrl;
                //               } else
                //               {
                usedHash = member.AvatarHash;
                usedUrl = member.GetAvatarUrl(ImageFormat.Png);
                //                }

                if (db.HashGet("safeAvatars", usedHash) == true)
                {
                    discord.Logger.LogDebug("Unnecessary avatar check skipped for " + member.Id);
                    return false;
                }

                var builder = new UriBuilder("https://ravy.org/api/v1/avatars");
                var query = HttpUtility.ParseQueryString(builder.Query);
                query["avatar"] = usedUrl;
                builder.Query = query.ToString();
                string url = builder.ToString();

                HttpRequestMessage request = new(HttpMethod.Get, url);
                request.Headers.Add("User-Agent", "Cliptok (https://github.com/Erisa/Cliptok)");
                request.Headers.Add("Authorization", Environment.GetEnvironmentVariable("RAVY_API_TOKEN"));

                HttpResponseMessage response = await httpClient.SendAsync(request);
                int httpStatusCode = (int)response.StatusCode;
                var httpStatus = response.StatusCode;
                string responseText = await response.Content.ReadAsStringAsync();

                if (httpStatus == System.Net.HttpStatusCode.OK)
                {
                    var avatarResponse = JsonConvert.DeserializeObject<AvatarResponseBody>(responseText);
                    discord.Logger.LogInformation($"Avatar check for {member.Id}: {httpStatusCode} {responseText}");

                    if (avatarResponse.Matched && avatarResponse.Key != "logo")
                    {
                        var embed = new DiscordEmbedBuilder()
                            .WithDescription($"API Response:\n```json\n{responseText}\n```")
                            .WithAuthor($"{member.Username}#{member.Discriminator}", null, usedUrl)
                            .WithFooter($"User ID: {member.Id}")
                            .WithImageUrl(await LykosAvatarMethods.UserOrMemberAvatarURL(member, member.Guild, "default", 256));

                        await badMsgLog.SendMessageAsync($"{cfgjson.Emoji.Banned} {member.Mention} has been appeal-banned for an infringing avatar.", embed);
                        await Bans.BanFromServerAsync(member.Id, "Automatic ban for matching patterns of common bot/compromised accounts. Please appeal if you are human.", discord.CurrentUser.Id, member.Guild, 7, appealable: true);
                        return true;
                    }
                    else if (!avatarResponse.Matched)
                    {
                        await db.HashSetAsync("safeAvatars", usedHash, true);
                        return false;
                    }
                }
                else
                {
                    discord.Logger.LogError($"Avatar check for {member.Id}: {httpStatusCode} {responseText}");
                }

                return false;
            }

            async Task UserUpdated(DiscordClient client, UserUpdateEventArgs e)
            {
                Task.Run(async () =>
                {
                    var member = await homeGuild.GetMemberAsync(e.UserAfter.Id);

                    CheckAndDehoistMemberAsync(member);
                    UsernameCheckAsync(member);
                });
            }

            async Task MessageCreated(DiscordClient client, MessageCreateEventArgs e)
            {
                MessageEvent.MessageHandlerAsync(client, e.Message, e.Channel);
            }

            async Task MessageUpdated(DiscordClient client, MessageUpdateEventArgs e)
            {
                MessageEvent.MessageHandlerAsync(client, e.Message, e.Channel, true);
            }

            async Task CommandsNextService_CommandErrored(CommandsNextExtension cnext, CommandErrorEventArgs e)
            {
                if (e.Exception is CommandNotFoundException && (e.Command == null || e.Command.QualifiedName != "help"))
                    return;

                // avoid conflicts with modmaail
                if (e.Command.QualifiedName == "edit")
                    return;

                e.Context.Client.Logger.LogError(CliptokEventID, e.Exception, "Exception occurred during {0}'s invocation of '{1}'", e.Context.User.Username, e.Context.Command.QualifiedName);

                var exs = new List<Exception>();
                if (e.Exception is AggregateException ae)
                    exs.AddRange(ae.InnerExceptions);
                else
                    exs.Add(e.Exception);

                foreach (var ex in exs)
                {
                    if (ex is CommandNotFoundException && (e.Command == null || e.Command.QualifiedName != "help"))
                        return;

                    if (ex is ChecksFailedException && (e.Command.Name != "help"))
                        return;

                    var embed = new DiscordEmbedBuilder
                    {
                        Color = new DiscordColor("#FF0000"),
                        Title = "An exception occurred when executing a command",
                        Description = $"{cfgjson.Emoji.BSOD} `{e.Exception.GetType()}` occurred when executing `{e.Command.QualifiedName}`.",
                        Timestamp = DateTime.UtcNow
                    };
                    embed.WithFooter(discord.CurrentUser.Username, discord.CurrentUser.AvatarUrl)
                        .AddField("Message", ex.Message);
                    if (e.Exception is System.ArgumentException)
                        embed.AddField("Note", "This usually means that you used the command incorrectly.\n" +
                            "Please double-check how to use this command.");
                    await e.Context.RespondAsync(embed: embed.Build()).ConfigureAwait(false);
                }
            }

            Task Discord_ThreadCreated(DiscordClient client, ThreadCreateEventArgs e)
            {
                e.Thread.JoinThreadAsync();
                client.Logger.LogDebug(eventId: CliptokEventID, $"Thread created in {e.Guild.Name}. Thread Name: {e.Thread.Name}");
                return Task.CompletedTask;
            }

            Task Discord_ThreadUpdated(DiscordClient client, ThreadUpdateEventArgs e)
            {
                client.Logger.LogDebug(eventId: CliptokEventID, $"Thread updated in {e.Guild.Name}. New Thread Name: {e.ThreadAfter.Name}");
                return Task.CompletedTask;
            }

            Task Discord_ThreadDeleted(DiscordClient client, ThreadDeleteEventArgs e)
            {
                client.Logger.LogDebug(eventId: CliptokEventID, $"Thread deleted in {e.Guild.Name}. Thread Name: {e.Thread.Name ?? "Unknown"}");
                return Task.CompletedTask;
            }

            Task Discord_ThreadListSynced(DiscordClient client, ThreadListSyncEventArgs e)
            {
                client.Logger.LogDebug(eventId: CliptokEventID, $"Threads synced in {e.Guild.Name}.");
                return Task.CompletedTask;
            }

            Task Discord_ThreadMemberUpdated(DiscordClient client, ThreadMemberUpdateEventArgs e)
            {
                client.Logger.LogDebug(eventId: CliptokEventID, $"Thread member updated.");
                client.Logger.LogDebug(CliptokEventID, $"Discord_ThreadMemberUpdated fired for thread {e.ThreadMember.ThreadId}. User ID {e.ThreadMember.Id}.");
                return Task.CompletedTask;
            }

            Task Discord_ThreadMembersUpdated(DiscordClient client, ThreadMembersUpdateEventArgs e)
            {
                client.Logger.LogDebug(eventId: CliptokEventID, $"Thread members updated in {e.Guild.Name}.");
                return Task.CompletedTask;
            }

            Task Discord_SocketErrored(DiscordClient client, SocketErrorEventArgs e)
            {
                client.Logger.LogError(eventId: CliptokEventID, e.Exception, "A socket error ocurred!");
                return Task.CompletedTask;
            }

            discord.MessageReactionAdded += async (s, e) =>
            {
                if (DateTime.Now.Month == 4 && DateTime.Now.Day == 1 && DateTime.Now.Year == 2022)
                {

                    if (!e.User.IsBot && e.Emoji.Id == 959169120421703760)
                    {

                        var list = await db.ListRangeAsync("2022-aprilfools-hydrate-messages", 0, -1);
                        var messageCount = (int)db.StringGet("2022-aprilfools-message-count");

                        if (messageCount == 1 && list[0] == e.Message.Id)
                        {
                            await db.SetAddAsync($"2022-aprilfools-hydration-{e.User.Id}", e.Message.Id);
                        }
                        else
                        {
                            if (list[messageCount - 1] == e.Message.Id || list[messageCount - 2] == e.Message.Id)
                            {
                                await db.SetAddAsync($"2022-aprilfools-hydration-{e.User.Id}", e.Message.Id);
                            }
                        }

                    }
                }
            };

            discord.ComponentInteractionCreated += async (s, e) =>
            {
                // Initial response to avoid the 3 second timeout, will edit later.
                var eout = new DiscordInteractionResponseBuilder().AsEphemeral(true);
                await e.Interaction.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource, eout);

                // Edits need a webhook rather than interaction..?
                DiscordWebhookBuilder webhookOut;

                if (e.Id == "line-limit-deleted-message-callback")
                {
                    var text = await Program.db.HashGetAsync("deletedMessageReferences", e.Message.Id);
                    if (text.IsNullOrEmpty)
                    {
                        webhookOut = new DiscordWebhookBuilder().WithContent("I couldn't find any content for that message! This might be a bug?");
                        await e.Interaction.EditOriginalResponseAsync(webhookOut);
                    }
                    else
                    {
                        DiscordEmbedBuilder embed = new();
                        embed.Description = text;
                        embed.Title = "Deleted message content";
                        webhookOut = new DiscordWebhookBuilder().AddEmbed(embed);
                        await e.Interaction.EditOriginalResponseAsync(webhookOut);
                    }

                }
                else
                {

                }

            };

            discord.Ready += OnReady;
            discord.MessageCreated += MessageCreated;
            discord.MessageUpdated += MessageUpdated;
            discord.GuildMemberAdded += GuildMemberAdded;
            discord.GuildMemberRemoved += GuildMemberRemoved;
            discord.MessageReactionAdded += OnReaction;
            discord.GuildMemberUpdated += GuildMemberUpdated;
            discord.UserUpdated += UserUpdated;
            discord.ClientErrored += ClientError;
            discord.SocketErrored += Discord_SocketErrored;

            discord.ThreadCreated += Discord_ThreadCreated;
            discord.ThreadUpdated += Discord_ThreadUpdated;
            discord.ThreadDeleted += Discord_ThreadDeleted;
            discord.ThreadListSynced += Discord_ThreadListSynced;
            discord.ThreadMemberUpdated += Discord_ThreadMemberUpdated;
            discord.ThreadMembersUpdated += Discord_ThreadMembersUpdated;

            commands = discord.UseCommandsNext(new CommandsNextConfiguration
            {
                StringPrefixes = cfgjson.Core.Prefixes
            });

            commands.RegisterCommands<Warnings>();
            commands.RegisterCommands<MuteCmds>();
            commands.RegisterCommands<UserRoleCmds>();
            commands.RegisterCommands<ModCmds>();
            commands.RegisterCommands<Lockdown>();
            commands.RegisterCommands<Bans>();
            commands.CommandErrored += CommandsNextService_CommandErrored;

            await discord.ConnectAsync();

            // Only wait 3 seconds before the first set of tasks.
            await Task.Delay(3000);

            var generalChat = await discord.GetChannelAsync(150662382874525696);

            Task.Run(async () =>
            {
                while (true)
                {
                    if (DateTime.Now.Month == 4 && DateTime.Now.Day == 1 && DateTime.Now.Year == 2022)
                    {
                        int messageCount = 0;
                        if (db.KeyExists("2022-aprilfools-message-count"))
                        {
                            messageCount = (int)db.StringGet("2022-aprilfools-message-count");
                        }

                        var msg = await generalChat.SendMessageAsync(waterList[messageCount]);

                        db.StringSet("2022-aprilfools-message-count", messageCount + 1);

                        await db.ListRightPushAsync("2022-aprilfools-hydrate-messages", msg.Id);
                        var emoji = await homeGuild.GetEmojiAsync(959169120421703760);
                        await msg.CreateReactionAsync(emoji);
                    }
                    await Task.Delay(rand.Next(1800000, 2700000)); // 30-45 minutes
                }
            });

            while (true)
            {
                try
                {
                    List<Task<bool>> taskList = new();
                    taskList.Add(Mutes.CheckMutesAsync());
                    taskList.Add(ModCmds.CheckBansAsync());
                    taskList.Add(ModCmds.CheckRemindersAsync());
                    taskList.Add(ModCmds.CheckRaidmodeAsync(cfgjson.ServerID));
                    taskList.Add(Lockdown.CheckUnlocksAsync());

                    // To prevent a future issue if checks take longer than 10 seconds,
                    // we only start the 10 second counter after all tasks have concluded.
                    await Task.WhenAll(taskList);
                }
                catch (Exception e)
                {
                    discord.Logger.LogError(CliptokEventID, message: e.ToString());
                }
                await Task.Delay(10000);
            }
        }
    }
}
