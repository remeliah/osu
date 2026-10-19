// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Bindables;
using osu.Game.Online.Notifications.WebSocket;

namespace osu.Game.Online.Bancho
{
    internal sealed class LegacyNotificationsClient : INotificationsClient
    {
        public IBindable<bool> IsConnected { get; } = new BindableBool(false);

#pragma warning disable CS0067
        public event Action<SocketMessage>? MessageReceived;
#pragma warning restore CS0067

        public Task SendAsync(SocketMessage message, CancellationToken? cancellationToken = null) =>
            Task.FromException(new NotSupportedException("Web notifications are not part of the osu!stable Bancho protocol."));
    }
}
