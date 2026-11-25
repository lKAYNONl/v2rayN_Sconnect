namespace ServiceLib.Models.Dto;

/// <summary>
/// A subscription shown as a collapsible folder in the profiles rail of the
/// Secure Connect window. Profiles that belong to no subscription have no
/// folder and are listed on their own.
/// </summary>
[Serializable]
public partial class ProfileGroupModel : ReactiveObject
{
    /// <summary>Subscription id.</summary>
    public string Id { get; set; }

    public string Remarks { get; set; }

    [Reactive]
    public partial int Count { get; set; }

    [Reactive]
    public partial bool Expanded { get; set; }

    /// <summary>The folder is collapsed and the active profile is inside it.</summary>
    [Reactive]
    public partial bool ActiveHidden { get; set; }
}
