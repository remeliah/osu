// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Net.Http;
using osu.Framework.IO.Network;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Chat;

namespace osu.Game.Online.API.Requests
{
    public class CreateNewPrivateMessageRequest : APIRequest<CreateNewPrivateMessageResponse>
    {
        public readonly APIUser TargetUser;
        public readonly Message OutgoingMessage;

        public CreateNewPrivateMessageRequest(APIUser user, Message message)
        {
            TargetUser = user;
            OutgoingMessage = message;
        }

        protected override WebRequest CreateWebRequest()
        {
            var req = base.CreateWebRequest();
            req.Method = HttpMethod.Post;
            req.AddParameter(@"target_id", TargetUser.Id.ToString());
            req.AddParameter(@"message", OutgoingMessage.Content);
            req.AddParameter(@"is_action", OutgoingMessage.IsAction.ToString().ToLowerInvariant());
            req.AddParameter(@"uuid", OutgoingMessage.Uuid);
            return req;
        }

        protected override string Target => @"chat/new";
    }
}
