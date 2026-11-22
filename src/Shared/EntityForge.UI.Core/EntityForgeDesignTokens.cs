namespace EntityForge.UI;

/// <summary>
/// Framework-neutral dimensions shared by EntityForge products. Colours are exposed as semantic
/// resource names by each renderer so application code never depends on a literal palette.
/// </summary>
public static class EntityForgeDesignTokens
{
    public static class Radius
    {
        public const double Small = 6;
        public const double Medium = 10;
        public const double Large = 16;
    }

    public static class Space
    {
        public const double XSmall = 4;
        public const double Small = 8;
        public const double Medium = 12;
        public const double Large = 20;
        public const double XLarge = 28;
    }

    public static class Type
    {
        public const double Caption = 11;
        public const double Body = 13;
        public const double BodyLarge = 15;
        public const double Title = 20;
        public const double Display = 28;
    }

    public static class Resource
    {
        public const string Canvas = "EntityForge.Brush.Canvas";
        public const string Surface = "EntityForge.Brush.Surface";
        public const string SurfaceRaised = "EntityForge.Brush.SurfaceRaised";
        public const string SurfaceInteractive = "EntityForge.Brush.SurfaceInteractive";
        public const string Border = "EntityForge.Brush.Border";
        public const string PanelBorder = "EntityForge.Brush.PanelBorder";
        public const string BorderStrong = "EntityForge.Brush.BorderStrong";
        public const string Text = "EntityForge.Brush.Text";
        public const string TextMuted = "EntityForge.Brush.TextMuted";
        public const string Accent = "EntityForge.Brush.Accent";
        public const string AccentHover = "EntityForge.Brush.AccentHover";
        public const string AccentText = "EntityForge.Brush.AccentText";
        public const string Success = "EntityForge.Brush.Success";
        public const string Warning = "EntityForge.Brush.Warning";
        public const string Danger = "EntityForge.Brush.Danger";
        public const string Evidence = "EntityForge.Brush.Evidence";
        public const string Focus = "EntityForge.Brush.Focus";
    }
}
