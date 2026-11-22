using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace EntityForge.UI.Avalonia;

/// <summary>Applies EntityForge semantic palette resources to an Avalonia application.</summary>
public static class EntityForgeThemeManager
{
    public static EntityForgeTheme Current { get; private set; } = EntityForgeTheme.System;

    public static void Apply(Application application, EntityForgeTheme theme)
    {
        Current = theme;
        application.RequestedThemeVariant = theme switch
        {
            EntityForgeTheme.Light => ThemeVariant.Light,
            EntityForgeTheme.Dark or EntityForgeTheme.Blue => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        Palette palette = theme switch
        {
            EntityForgeTheme.Light => Palette.Light,
            EntityForgeTheme.Blue => Palette.Blue,
            EntityForgeTheme.Dark => Palette.Dark,
            _ => application.ActualThemeVariant == ThemeVariant.Light ? Palette.Light : Palette.Dark,
        };

        foreach ((string key, string colour) in palette.Colours)
        {
            application.Resources[key] = new SolidColorBrush(Color.Parse(colour));
        }

        application.Resources[EntityForgeBrandTokens.Resource.BrandGradient] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse(BrandPalette.Blue), 0),
                new GradientStop(Color.Parse(BrandPalette.Indigo), 0.52),
                new GradientStop(Color.Parse(BrandPalette.Violet), 1),
            },
        };
        application.Resources[EntityForgeBrandTokens.Resource.CodeFont] = new FontFamily("Cascadia Code, Consolas");
        application.Resources[EntityForgeBrandTokens.Resource.BodyFontSize] = 13d;
        application.Resources[EntityForgeBrandTokens.Resource.BodyFontWeight] = FontWeight.Normal;
        application.Resources[EntityForgeBrandTokens.Resource.ControlFontWeight] = FontWeight.Normal;
        application.Resources[EntityForgeBrandTokens.Resource.EmphasisFontWeight] = FontWeight.SemiBold;
    }

    private sealed record Palette(IReadOnlyDictionary<string, string> Colours)
    {
        public static Palette Dark
        {
            get;
        }
        = Create(
            canvas: "#111217", surface: "#181B1F", raised: "#202328", interactive: "#262A30",
            border: "#25282D", panelBorder: "#363B43", borderStrong: "#3A3E45", text: "#F3F4F6", muted: "#A2A5AD",
            accent: BrandPalette.Blue, accentHover: "#3478FF", accentText: "#FFFFFF",
            success: "#52D39A", warning: "#F2B84B", danger: "#FF6B7A", evidence: "#A78BFA",
            inactivePanelOverlay: "#B3090F19");

        public static Palette Blue
        {
            get;
        }
        = Create(
            canvas: "#061426", surface: "#0A2038", raised: "#102C4A", interactive: "#173A5E",
            border: "#173653", panelBorder: "#28516F", borderStrong: "#2B6388", text: "#F3F9FF", muted: "#9DB8D1",
            accent: BrandPalette.Blue, accentHover: "#3478FF", accentText: "#FFFFFF",
            success: "#58D8A0", warning: "#F7C15A", danger: "#FF7182", evidence: "#B69CFF",
            inactivePanelOverlay: "#B3090F19");

        public static Palette Light
        {
            get;
        }
        = Create(
            canvas: "#F2F5F8", surface: "#FFFFFF", raised: "#F8FAFC", interactive: "#EAF0F6",
            border: "#D5DEE8", panelBorder: "#BECBD8", borderStrong: "#AEBECD", text: "#152334", muted: "#5E7185",
            accent: BrandPalette.Blue, accentHover: "#024DCC", accentText: "#FFFFFF",
            success: "#087A50", warning: "#9A6500", danger: "#C43245", evidence: "#6842B8",
            inactivePanelOverlay: "#D9F2F5F8");

        private static Palette Create(
            string canvas, string surface, string raised, string interactive, string border, string panelBorder,
            string borderStrong, string text, string muted, string accent, string accentHover,
            string accentText, string success, string warning, string danger, string evidence,
            string inactivePanelOverlay)
            => new(new Dictionary<string, string>
            {
                [EntityForgeDesignTokens.Resource.Canvas] = canvas,
                [EntityForgeDesignTokens.Resource.Surface] = surface,
                [EntityForgeDesignTokens.Resource.SurfaceRaised] = raised,
                [EntityForgeDesignTokens.Resource.SurfaceInteractive] = interactive,
                [EntityForgeDesignTokens.Resource.Border] = border,
                [EntityForgeDesignTokens.Resource.PanelBorder] = panelBorder,
                [EntityForgeDesignTokens.Resource.BorderStrong] = borderStrong,
                [EntityForgeDesignTokens.Resource.Text] = text,
                [EntityForgeDesignTokens.Resource.TextMuted] = muted,
                [EntityForgeDesignTokens.Resource.Accent] = accent,
                [EntityForgeDesignTokens.Resource.AccentHover] = accentHover,
                [EntityForgeDesignTokens.Resource.AccentText] = accentText,
                [EntityForgeDesignTokens.Resource.Success] = success,
                [EntityForgeDesignTokens.Resource.Warning] = warning,
                [EntityForgeDesignTokens.Resource.Danger] = danger,
                [EntityForgeDesignTokens.Resource.Evidence] = evidence,
                [EntityForgeDesignTokens.Resource.Focus] = accent,
                [EntityForgeBrandTokens.Resource.BrandBlue] = BrandPalette.Blue,
                [EntityForgeBrandTokens.Resource.BrandIndigo] = BrandPalette.Indigo,
                [EntityForgeBrandTokens.Resource.BrandViolet] = BrandPalette.Violet,
                [EntityForgeBrandTokens.Resource.Semantic] = BrandPalette.Blue,
                [EntityForgeBrandTokens.Resource.Graph] = "#76D66B",
                [EntityForgeBrandTokens.Resource.Rdf] = "#F2B84B",
                [EntityForgeBrandTokens.Resource.Overlay] = "#B3090F19",
                [EntityForgeBrandTokens.Resource.InactivePanelOverlay] = inactivePanelOverlay,
            });
    }

    private static class BrandPalette
    {
        public const string Blue = "#025FFC";
        public const string Indigo = "#4C44FA";
        public const string Violet = "#8535FA";
    }
}
