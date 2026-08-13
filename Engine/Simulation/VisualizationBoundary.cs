namespace ExoInstruments.Visualization
{
    /// <summary>
    /// BOUNDARY STUB, not a design choice.
    ///
    /// Core/VisualTelescopeCatalog.cs opens with `using ExoInstruments.Visualization;` because
    /// VisualTelescopeSpec.AvailableFilters is a CameraFilter[]. The enum itself is pure data,
    /// but it is declared inside Visualization/SolarSystemCameraTexture.cs, which is 6400 lines
    /// of UnityEngine and KSP camera cloning. So Core cannot compile without Unity over one
    /// enum, and this file supplies it.
    ///
    /// THE REAL FIX, worth doing in the mod after the release: move this enum into Core
    /// (Core/EmissionLines already owns the physics its narrowband members refer to) and delete
    /// this file. It is a cut-and-paste with no behaviour change, and it removes the last
    /// non-Unity reason Core is not standalone.
    ///
    /// Until then, Verify asserts member-for-member that this declaration still matches the
    /// mod's, so the two cannot drift apart unnoticed.
    /// </summary>
    public enum CameraFilter
    {
        Luminance,
        Red,
        Green,
        Blue,
        HAlpha,
        OIII,
        SII,
        NII,
        OII,
        OI
    }

}
