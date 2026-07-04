using System.ComponentModel.DataAnnotations;

namespace YMM4MinecraftAvatar;

public enum MinecraftEdition
{
    [Display(Name = "Java版")]
    Java,

    [Display(Name = "統合版 (Bedrock)")]
    Bedrock,
}
