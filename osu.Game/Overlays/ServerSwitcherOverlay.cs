// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Configuration;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Cursor;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online.API;
using osu.Game.Overlays.Settings;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Overlays
{
    /// <summary>
    /// A standalone private-server switcher. Values are staged locally and are only
    /// applied when the switch button is pressed.
    /// </summary>
    public partial class ServerSwitcherOverlay : OsuFocusedOverlayContainer, INamedOverlayComponent
    {
        public IconUsage Icon => FontAwesome.Solid.Globe;
        public LocalisableString Title => "Server Switcher";
        public LocalisableString Description => "switch endpoints";

        private const float panel_width = 360;
        private const float transition_duration = 400;

        private readonly Bindable<string> serverDomain = new();
        private readonly BindableBool useAdvancedEndpoints = new();
        private readonly Bindable<string> banchoUrl = new();
        private readonly Bindable<string> websiteUrl = new();
        private readonly Bindable<string> avatarUrl = new();

        private OsuConfigManager config = null!;
        private LegacyBanchoAPIAccess api = null!;
        private SettingsItemV2 serverItem = null!;
        private FillFlowContainer advancedContainer = null!;
        private Drawable[] advancedItems = Array.Empty<Drawable>();

        [Cached]
        private readonly OverlayColourProvider colourProvider = new(OverlayColourScheme.Purple);

        public ServerSwitcherOverlay()
        {
            AutoSizeAxes = Axes.Both;
            // The floating overlay container is already offset below the toolbar.
            Margin = new MarginPadding { Left = 10 };
            Masking = true;
            EdgeEffect = new EdgeEffectParameters
            {
                Colour = Color4.Black,
                Type = EdgeEffectType.Shadow,
                Radius = 10,
                Hollow = true,
            };
        }

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config, IAPIProvider api)
        {
            this.config = config;
            this.api = api as LegacyBanchoAPIAccess
                       ?? throw new InvalidOperationException("The server switcher requires the stable Bancho adapter.");

            serverItem = createTextItem("Server", "Base domain only; https:// is optional.", "local.sh", serverDomain);
            SettingsItemV2 banchoItem = createTextItem(
                "Bancho / cho server",
                "Login, presence, chat, multiplayer, and spectator packets. Blank uses c.<base domain>.",
                "c.local.sh",
                banchoUrl);
            SettingsItemV2 websiteItem = createTextItem(
                "osu! / web server",
                "Score submission, leaderboard, and stable web endpoints. Blank uses osu.<base domain>.",
                "osu.local.sh",
                websiteUrl);
            SettingsItemV2 avatarItem = createTextItem(
                "Avatar server",
                "Avatar and user image origin. Blank uses a.<base domain>.",
                "a.local.sh",
                avatarUrl);

            advancedItems = new Drawable[] { banchoItem, websiteItem, avatarItem };

            InternalChild = new OsuContextMenuContainer
            {
                Width = panel_width,
                AutoSizeAxes = Axes.Y,
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = colourProvider.Background4,
                    },
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Masking = true,
                        AutoSizeDuration = transition_duration,
                        AutoSizeEasing = Easing.OutQuint,
                        Child = new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Padding = new MarginPadding { Vertical = SettingsSection.ITEM_SPACING },
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0, SettingsSection.ITEM_SPACING_V2),
                            Children = new Drawable[]
                            {
                                createHeader(),
                                serverItem,
                                new SettingsItemV2(new FormCheckBox
                                {
                                    Caption = "Advanced endpoints",
                                    HintText = "Override services when the server does not use standard c, osu, and a subdomains.",
                                    Current = useAdvancedEndpoints,
                                })
                                {
                                    ShowRevertToDefaultButton = false,
                                },
                                advancedContainer = new FillFlowContainer
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Direction = FillDirection.Vertical,
                                    Spacing = new Vector2(0, SettingsSection.ITEM_SPACING_V2),
                                },
                                new SettingsButton
                                {
                                    Text = "Switch Server",
                                    Action = switchServer,
                                },
                            },
                        },
                    },
                },
            };

            useAdvancedEndpoints.BindValueChanged(change => updateAdvancedVisibility(change.NewValue), true);
        }

        private Drawable createHeader() => new Container
        {
            RelativeSizeAxes = Axes.X,
            Height = 64,
            Padding = new MarginPadding { Horizontal = 18 },
            Children = new Drawable[]
            {
                new SpriteIcon
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Icon = Icon,
                    Size = new Vector2(24),
                    Colour = colourProvider.Content1,
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Vertical,
                    Margin = new MarginPadding { Left = 38 },
                    Children = new Drawable[]
                    {
                        new OsuSpriteText
                        {
                            Text = Title,
                            Font = OsuFont.GetFont(size: 22, weight: FontWeight.Bold),
                        },
                        new OsuSpriteText
                        {
                            Text = Description,
                            Font = OsuFont.GetFont(size: 13),
                            Colour = colourProvider.Content2,
                        },
                    },
                },
            },
        };

        private static SettingsItemV2 createTextItem(string caption, string hint, string placeholder, Bindable<string> current) => new(new FormTextBox
        {
            Caption = caption,
            HintText = hint,
            PlaceholderText = placeholder,
            Current = current,
            SelectAllOnFocus = true,
        })
        {
            ShowRevertToDefaultButton = false,
        };

        private void updateAdvancedVisibility(bool visible)
        {
            advancedContainer.Clear(false);

            if (visible)
                advancedContainer.AddRange(advancedItems);
        }

        private void reloadFromConfig()
        {
            serverDomain.Value = config.Get<string>(OsuSetting.PrivateServerUrl);
            useAdvancedEndpoints.Value = config.Get<bool>(OsuSetting.PrivateServerAdvancedEndpoints);
            banchoUrl.Value = config.Get<string>(OsuSetting.PrivateServerBanchoUrl);
            websiteUrl.Value = config.Get<string>(OsuSetting.PrivateServerWebsiteUrl);
            avatarUrl.Value = config.Get<string>(OsuSetting.PrivateServerAvatarUrl);
            serverItem.Note.Value = null;
        }

        private void switchServer()
        {
            Exception error = api.SwitchServer(serverDomain.Value, useAdvancedEndpoints.Value, banchoUrl.Value, websiteUrl.Value, avatarUrl.Value);

            if (error != null)
            {
                serverItem.Note.Value = new SettingsNote.Data(error.Message, SettingsNote.Type.Critical);
                return;
            }

            reloadFromConfig();
            serverItem.Note.Value = new SettingsNote.Data(
                string.IsNullOrEmpty(serverDomain.Value)
                    ? "Private server disabled."
                    : $"Switched to {serverDomain.Value}. Reconnecting Bancho now...",
                SettingsNote.Type.Informational);
        }

        protected override void PopIn()
        {
            reloadFromConfig();
            this.FadeIn(transition_duration, Easing.OutQuint);
            FadeEdgeEffectTo(WaveContainer.SHADOW_OPACITY, WaveContainer.APPEAR_DURATION, Easing.Out);
        }

        protected override void PopOut()
        {
            base.PopOut();
            this.FadeOut(transition_duration);
            FadeEdgeEffectTo(0, WaveContainer.DISAPPEAR_DURATION, Easing.In);
        }
    }
}
