// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Game.Online.Chat;

namespace osu.Game.Online.Bancho
{
    internal sealed class LegacyBanchoChatClient : IChatClient
    {
        private readonly Func<bool> isOnline;
        private bool presenceRequested;

        public event Action<Channel>? ChannelJoined;
        public event Action<Channel>? ChannelParted;
        public event Action<List<Message>>? NewMessages;
        public event Action? PresenceReceived;

        public LegacyBanchoChatClient(Func<bool> isOnline)
        {
            this.isOnline = isOnline;
        }

        public void RequestPresence()
        {
            presenceRequested = true;
            if (isOnline())
                PresenceReceived?.Invoke();
        }

        public void NotifyPresenceReceived()
        {
            if (presenceRequested)
                PresenceReceived?.Invoke();
        }

        public void NotifyChannelJoined(Channel channel) => ChannelJoined?.Invoke(channel);

        public void NotifyChannelParted(Channel channel) => ChannelParted?.Invoke(channel);

        public void NotifyMessages(List<Message> messages) => NewMessages?.Invoke(messages);

        public void Dispose()
        {
        }
    }
}
