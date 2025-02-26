﻿namespace Cliptok.Commands
{
    class Bans : BaseCommandModule
    {

        public static async Task<bool> BanFromServerAsync(ulong targetUserId, string reason, ulong moderatorId, DiscordGuild guild, int deleteDays = 7, DiscordChannel channel = null, TimeSpan banDuration = default, bool appealable = false)
        {
            bool permaBan = false;
            DateTime? actionTime = DateTime.Now;
            DateTime? expireTime = actionTime + banDuration;
            DiscordMember moderator = await guild.GetMemberAsync(moderatorId);

            if (banDuration == default)
            {
                permaBan = true;
                expireTime = null;
            }

            MemberPunishment newBan = new()
            {
                MemberId = targetUserId,
                ModId = moderatorId,
                ServerId = guild.Id,
                ExpireTime = expireTime,
                ActionTime = actionTime,
                Reason = reason
            };

            await Program.db.HashSetAsync("bans", targetUserId, JsonConvert.SerializeObject(newBan));

            try
            {
                DiscordMember targetMember = await guild.GetMemberAsync(targetUserId);
                if (permaBan)
                {
                    if (appealable)
                    {
                        await targetMember.SendMessageAsync($"{Program.cfgjson.Emoji.Banned} You have been banned from **{guild.Name}**!\nReason: **{reason}**\nYou can appeal the ban here: <{Program.cfgjson.AppealLink}>");
                    }
                    else
                    {
                        await targetMember.SendMessageAsync($"{Program.cfgjson.Emoji.Banned} You have been permanently banned from **{guild.Name}**!\nReason: **{reason}**");
                    }
                }
                else
                {
                    await targetMember.SendMessageAsync($"{Program.cfgjson.Emoji.Banned} You have been banned from **{guild.Name}** for {TimeHelpers.TimeToPrettyFormat(banDuration, false)}!\nReason: **{reason}**\nBan expires: <t:{TimeHelpers.ToUnixTimestamp(expireTime)}:R>");
                }
            }
            catch
            {
                // A DM failing to send isn't important.
            }

            try
            {
                string logOut;
                await guild.BanMemberAsync(targetUserId, deleteDays, reason);
                if (permaBan)
                {
                    if (appealable)
                    {
                        logOut = $"{Program.cfgjson.Emoji.Banned} <@{targetUserId}> was permanently banned (with appeal) by `{moderator.Username}#{moderator.Discriminator}` (`{moderatorId}`).\nReason: **{reason}**";
                    }
                    else
                    {
                        logOut = $"{Program.cfgjson.Emoji.Banned} <@{targetUserId}> was permanently banned by `{moderator.Username}#{moderator.Discriminator}` (`{moderatorId}`).\nReason: **{reason}**";
                    }
                }
                else
                {
                    logOut = $"{Program.cfgjson.Emoji.Banned} <@{targetUserId}> was banned for {TimeHelpers.TimeToPrettyFormat(banDuration, false)} by `{moderator.Username}#{moderator.Discriminator}` (`{moderatorId}`).\nReason: **{reason}**\nBan expires: <t:{TimeHelpers.ToUnixTimestamp(expireTime)}:R>";
                }
                _ = LogChannelHelper.LogMessageAsync("mod", logOut);

                if (channel != null)
                    logOut += $"\nChannel: {channel.Mention}";

                _ = FindModmailThreadAndSendMessage(guild, $"User ID: {targetUserId}", logOut);
            }
            catch
            {
                return false;
            }
            return true;

        }

        public static async Task FindModmailThreadAndSendMessage(DiscordGuild guild, string searchText, string messageToSend)
        {
            var matchPair = guild.Channels.FirstOrDefault(c => c.Value.Type == ChannelType.Text && c.Value.Topic != null && c.Value.Topic.EndsWith(searchText));
            var channel = matchPair.Value;

            if (channel != default)
                await channel.SendMessageAsync(messageToSend);
        }


        public static async Task UnbanFromServerAsync(DiscordGuild targetGuild, ulong targetUserId)
        {
            try
            {
                DiscordUser user = await Program.discord.GetUserAsync(targetUserId);
                await targetGuild.UnbanMemberAsync(user, "Temporary ban expired");
            }
            catch
            {
                await LogChannelHelper.LogMessageAsync("mod",
                    new DiscordMessageBuilder()
                        .WithContent($"{Program.cfgjson.Emoji.Denied} Attempt to unban <@{targetUserId}> failed!\nMaybe they were already unbanned?")
                        .WithAllowedMentions(Mentions.None)
                    );
            }
            // Even if the bot failed to unban, it reported that failure to a log channel and thus the ban record
            //  can be safely removed internally.
            await Program.db.HashDeleteAsync("bans", targetUserId);
        }

        public async static Task<bool> UnbanUserAsync(DiscordGuild guild, DiscordUser target, string reason = "")
        {
            await Program.db.HashSetAsync("unbanned", target.Id, true);
            try
            {
                await guild.UnbanMemberAsync(user: target, reason: reason);
            }
            catch (Exception e)
            {
                Program.discord.Logger.LogError(Program.CliptokEventID, e, "An exception occurred while unbanning {user}", target.Id);
                return false;
            }
            await LogChannelHelper.LogMessageAsync("mod",new DiscordMessageBuilder().WithContent($"{Program.cfgjson.Emoji.Unbanned} Successfully unbanned {target.Mention}!").WithAllowedMentions(Mentions.None));
            await Program.db.HashDeleteAsync("bans", target.Id.ToString());
            return true;
        }

        public static async Task<bool> BanSilently(DiscordGuild targetGuild, ulong targetUserId, string reason = "Mass ban")
        {
            try
            {
                await targetGuild.BanMemberAsync(targetUserId, 7, reason);
                return true;
            }
            catch
            {
                return false;
            }

        }

        [Command("massban")]
        [Aliases("bigbonk")]
        [HomeServer, RequireHomeserverPerm(ServerPermLevel.Moderator)]
        public async Task MassBanCmd(CommandContext ctx, [RemainingText] string input)
        {

            List<string> usersString = input.Replace("\n", " ").Replace("\r", "").Split(' ').ToList();
            List<ulong> users = usersString.Select(x => Convert.ToUInt64(x)).ToList();
            if (users.Count == 1 || users.Count == 0)
            {
                await ctx.RespondAsync($"{Program.cfgjson.Emoji.Error} Not accepting a massban with a single user. Please use `!ban`.");
                return;
            }

            List<Task<bool>> taskList = new();
            int successes = 0;

            var loading = await ctx.RespondAsync("Processing, please wait.");

            foreach (ulong user in users)
            {
                taskList.Add(BanSilently(ctx.Guild, user));
            }

            var tasks = await Task.WhenAll(taskList);

            foreach (var task in taskList)
            {
                if (task.Result)
                    successes += 1;
            }

            await ctx.RespondAsync($"{Program.cfgjson.Emoji.Banned} **{successes}**/{users.Count} users were banned successfully.");
            await loading.DeleteAsync();
        }

        [Command("ban")]
        [Aliases("tempban", "bonk")]
        [Description("Bans a user that you have permission to ban, deleting all their messages in the process. See also: bankeep.")]
        [HomeServer, RequireHomeserverPerm(ServerPermLevel.Moderator), RequirePermissions(Permissions.BanMembers)]
        public async Task BanCmd(CommandContext ctx,
     [Description("The user you wish to ban. Accepts many formats")] DiscordUser targetMember,
     [RemainingText, Description("The time and reason for the ban. e.g. '14d trolling' NOTE: Add 'appeal' to the start of the reason to include an appeal link")] string timeAndReason = "No reason specified.")
        {
            bool appealable = false;
            bool timeParsed = false;

            TimeSpan banDuration = default;
            string possibleTime = timeAndReason.Split(' ').First();
            try
            {
                banDuration = HumanDateParser.HumanDateParser.Parse(possibleTime).Subtract(ctx.Message.Timestamp.DateTime);
                timeParsed = true;
            }
            catch
            {
                // keep default
            }

            string reason = timeAndReason;

            if (timeParsed)
            {
                int i = reason.IndexOf(" ") + 1;
                reason = reason[i..];
            }

            if (timeParsed && possibleTime == reason)
                reason = "No reason specified.";

            if (reason.Length > 6 && reason[..7].ToLower() == "appeal ")
            {
                appealable = true;
                reason = reason[7..^0];
            }

            DiscordMember member;
            try
            {
                member = await ctx.Guild.GetMemberAsync(targetMember.Id);
            }
            catch
            {
                member = null;
            }

            if (member == null)
            {
                await ctx.Message.DeleteAsync();
                await BanFromServerAsync(targetMember.Id, reason, ctx.User.Id, ctx.Guild, 7, ctx.Channel, banDuration, appealable);
            }
            else
            {
                if (DiscordHelpers.AllowedToMod(ctx.Member, member))
                {
                    if (DiscordHelpers.AllowedToMod(await ctx.Guild.GetMemberAsync(ctx.Client.CurrentUser.Id), member))
                    {
                        await ctx.Message.DeleteAsync();
                        await BanFromServerAsync(targetMember.Id, reason, ctx.User.Id, ctx.Guild, 7, ctx.Channel, banDuration, appealable);
                    }
                    else
                    {
                        await ctx.RespondAsync($"{Program.cfgjson.Emoji.Error} I don't have permission to ban **{targetMember.Username}#{targetMember.Discriminator}**!");
                        return;
                    }
                }
                else
                {
                    await ctx.RespondAsync($"{Program.cfgjson.Emoji.Error} You don't have permission to ban **{targetMember.Username}#{targetMember.Discriminator}**!");
                    return;
                }
            }
            reason = reason.Replace("`", "\\`").Replace("*", "\\*");
            if (banDuration == default)
                await ctx.Channel.SendMessageAsync($"{Program.cfgjson.Emoji.Banned} {targetMember.Mention} has been banned: **{reason}**");
            else
                await ctx.Channel.SendMessageAsync($"{Program.cfgjson.Emoji.Banned} {targetMember.Mention} has been banned for **{TimeHelpers.TimeToPrettyFormat(banDuration, false)}**: **{reason}**");
        }

        /// I CANNOT find a way to do this as alias so I made it a separate copy of the command.
        /// Sue me, I beg you.
        [Command("bankeep")]
        [Aliases("bansave")]
        [Description("Bans a user but keeps their messages around."), HomeServer, RequireHomeserverPerm(ServerPermLevel.Moderator), RequirePermissions(Permissions.BanMembers)]
        public async Task BankeepCmd(CommandContext ctx,
        [Description("The user you wish to ban. Accepts many formats")] DiscordUser targetMember,
        [RemainingText, Description("The time and reason for the ban. e.g. '14d trolling' NOTE: Add 'appeal' to the start of the reason to include an appeal link")] string timeAndReason = "No reason specified.")
        {
            bool appealable = false;
            bool timeParsed = false;

            TimeSpan banDuration = default;
            string possibleTime = timeAndReason.Split(' ').First();
            try
            {
                banDuration = HumanDateParser.HumanDateParser.Parse(possibleTime).Subtract(ctx.Message.Timestamp.DateTime);
                timeParsed = true;
            }
            catch
            {
                // keep default
            }

            string reason = timeAndReason;

            if (timeParsed)
            {
                int i = reason.IndexOf(" ") + 1;
                reason = reason[i..];
            }

            if (timeParsed && possibleTime == reason)
                reason = "No reason specified.";

            if (reason.Length > 6 && reason[..7].ToLower() == "appeal ")
            {
                appealable = true;
                reason = reason[7..^0];
            }

            DiscordMember member;
            try
            {
                member = await ctx.Guild.GetMemberAsync(targetMember.Id);
            }
            catch
            {
                member = null;
            }

            if (member == null)
            {
                await ctx.Message.DeleteAsync();
                await BanFromServerAsync(targetMember.Id, reason, ctx.User.Id, ctx.Guild, 0, ctx.Channel, banDuration, appealable);
            }
            else
            {
                if (DiscordHelpers.AllowedToMod(ctx.Member, member))
                {
                    if (DiscordHelpers.AllowedToMod(await ctx.Guild.GetMemberAsync(ctx.Client.CurrentUser.Id), member))
                    {
                        await ctx.Message.DeleteAsync();
                        await BanFromServerAsync(targetMember.Id, reason, ctx.User.Id, ctx.Guild, 0, ctx.Channel, banDuration, appealable);
                    }
                    else
                    {
                        await ctx.RespondAsync($"{Program.cfgjson.Emoji.Error} I don't have permission to ban **{targetMember.Username}#{targetMember.Discriminator}**!");
                        return;
                    }
                }
                else
                {
                    await ctx.RespondAsync($"{Program.cfgjson.Emoji.Error} You don't have permission to ban **{targetMember.Username}#{targetMember.Discriminator}**!");
                    return;
                }
            }
            reason = reason.Replace("`", "\\`").Replace("*", "\\*");
            if (banDuration == default)
                await ctx.Channel.SendMessageAsync($"{Program.cfgjson.Emoji.Banned} {targetMember.Mention} has been banned: **{reason}**");
            else
                await ctx.Channel.SendMessageAsync($"{Program.cfgjson.Emoji.Banned} {targetMember.Mention} has been banned for **{TimeHelpers.TimeToPrettyFormat(banDuration, false)}**: **{reason}**");
        }

        [Command("unban")]
        [Description("Unbans a user who has been previously banned.")]
        [HomeServer, RequireHomeserverPerm(ServerPermLevel.Moderator), RequirePermissions(Permissions.BanMembers)]
        public async Task UnmuteCmd(CommandContext ctx, [Description("The user to unban, usually a mention or ID")] DiscordUser targetUser, [Description("Used in audit log only currently")] string reason = "No reason specified.")
        {
            if ((await Program.db.HashExistsAsync("bans", targetUser.Id)))
            {
                await UnbanUserAsync(ctx.Guild, targetUser, $"[Unban by {ctx.User.Username}#{ctx.User.Discriminator}]: {reason}");
                await ctx.RespondAsync($"{Program.cfgjson.Emoji.Unbanned} Successfully unbanned **{targetUser.Username}#{targetUser.Discriminator}**.");
            }
            else
            {
                bool banSuccess = await UnbanUserAsync(ctx.Guild, targetUser);
                if (banSuccess)
                    await ctx.RespondAsync($"{Program.cfgjson.Emoji.Unbanned} Successfully unbanned **{targetUser.Username}#{targetUser.Discriminator}**.");
                else
                {
                    await ctx.RespondAsync($"{Program.cfgjson.Emoji.Error} {ctx.Member.Mention}, that user doesn't appear to be banned, *and* an error occurred while attempting to unban them anyway.\nPlease contact the bot owner if this wasn't expected, the error has been logged.");
                }
            }
        }

    }
}
