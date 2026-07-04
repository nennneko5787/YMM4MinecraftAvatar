using System.ComponentModel.DataAnnotations;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Shape;
using YukkuriMovieMaker.Project;

namespace YMM4MinecraftAvatar;

internal class MinecraftAvatarShapeParameter : ShapeParameterBase
{
    MinecraftEdition edition = MinecraftEdition.Java;
    [Display(Name = "エディション", Description = "Java版(MCID) / 統合版(ゲーマータグ)")]
    [EnumComboBox]
    public MinecraftEdition Edition
    {
        get => edition;
        set => Set(ref edition, value);
    }

    string playerName = "";
    [Display(Name = "プレイヤー名", Description = "Java版はMCID (例: Notch)、統合版はXboxのゲーマータグ")]
    [TextEditor(AcceptsReturn = false)]
    public string PlayerName
    {
        get => playerName;
        set => Set(ref playerName, value);
    }

    bool includeHat = true;
    [Display(Name = "帽子レイヤー", Description = "頭の上のレイヤー(帽子)を重ねて表示します")]
    [ToggleSlider]
    public bool IncludeHat
    {
        get => includeHat;
        set => Set(ref includeHat, value);
    }

    [Display(Name = "サイズ", Description = "描画サイズ(px)")]
    [AnimationSlider("F0", "px", 8, 2048)]
    public Animation Size { get; } = new Animation(256, 1, 4096);

    public MinecraftAvatarShapeParameter() : this(null) { }

    public MinecraftAvatarShapeParameter(SharedDataStore? sharedData) : base(sharedData) { }

    public override IShapeSource CreateShapeSource(IGraphicsDevicesAndContext devices)
        => new MinecraftAvatarShapeSource(devices, this);

    internal void NotifySourceRefresh() => OnPropertyChanged(nameof(PlayerName));

    protected override IEnumerable<IAnimatable> GetAnimatables() => [Size];

    protected override void LoadSharedData(SharedDataStore store)
    {
        var data = store.Load<SharedData>();
        if (data is null) return;
        Edition = data.Edition;
        PlayerName = data.PlayerName;
        IncludeHat = data.IncludeHat;
        Size.CopyFrom(data.Size);
    }

    protected override void SaveSharedData(SharedDataStore store)
        => store.Save(new SharedData(this));

    public override IEnumerable<string> CreateShapeItemExoFilter(int keyFrameIndex, ExoOutputDescription desc) => [];
    public override IEnumerable<string> CreateMaskExoFilter(int keyFrameIndex, ExoOutputDescription desc, ShapeMaskExoOutputDescription shapeMaskDesc) => [];

    class SharedData
    {
        public MinecraftEdition Edition { get; set; } = MinecraftEdition.Java;
        public string PlayerName { get; set; } = "";
        public bool IncludeHat { get; set; } = true;
        public Animation Size { get; } = new Animation(256, 1, 4096);

        public SharedData() { }
        public SharedData(MinecraftAvatarShapeParameter p)
        {
            Edition = p.Edition;
            PlayerName = p.PlayerName;
            IncludeHat = p.IncludeHat;
            Size.CopyFrom(p.Size);
        }
    }
}
