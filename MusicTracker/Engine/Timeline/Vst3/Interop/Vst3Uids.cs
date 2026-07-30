using System;

namespace MusicTracker.Engine.Timeline.Vst3.Interop
{
    /// <summary>
    /// Identifiants uniques (FUID / IID) des interfaces Steinberg VST3 dont on a besoin.
    ///
    /// **Layout des GUIDs** : le SDK C++ VST3 définit ses FUIDs via la macro <c>INLINE_UID(a,b,c,d)</c>. Sous
    /// Windows (<c>COM_COMPATIBLE=1</c>, notre cas), les 16 octets sont rangés dans l'ORDRE COM classique — ce
    /// qui veut dire que <see cref="Guid"/> .NET les mappe directement pourvu qu'on convertisse a/b/c/d en la
    /// forme <c>Data1-Data2-Data3-Data4</c> attendue par le format string de <see cref="Guid"/> :
    ///   Data1 = a
    ///   Data2 = (b &gt;&gt; 16) &amp; 0xFFFF
    ///   Data3 = b &amp; 0xFFFF
    ///   Data4 = octets big-endian de (c, d)
    /// C'est cette conversion qui a été appliquée pour chaque constante ci-dessous ; les valeurs source
    /// (INLINE_UID) sont laissées en commentaire pour recouper avec <c>pluginterfaces/</c>.
    ///
    /// Toutes les interfaces héritent de FUnknown (équivalent COM IUnknown : QueryInterface/AddRef/Release
    /// dans cet ordre). Sur Windows, on peut donc utiliser <see cref="System.Runtime.InteropServices.ComImport"/>
    /// avec <c>InterfaceIsIUnknown</c> et laisser le marshaller CLR faire les vtable calls.
    /// </summary>
    public static class Vst3Uids
    {
        // INLINE_UID(0x22888DDB, 0x156E45AE, 0x8358B348, 0x08190625)
        public const string IPluginBase = "22888DDB-156E-45AE-8358-B34808190625";

        // INLINE_UID(0x7A4D811C, 0x52114A1F, 0xAED9D2EE, 0x0B43BF9F)
        public const string IPluginFactory = "7A4D811C-5211-4A1F-AED9-D2EE0B43BF9F";

        // INLINE_UID(0x0007B650, 0xF24B4C0B, 0xA464EDB9, 0xF00B2ABB)
        public const string IPluginFactory2 = "0007B650-F24B-4C0B-A464-EDB9F00B2ABB";

        // DECLARE_CLASS_IID(IPluginFactory3, 0x4555A2AB, 0xC1234E57, 0x9B122910, 0x36878931) — SDK 3.5+, kept for completeness
        public const string IPluginFactory3 = "4555A2AB-C123-4E57-9B12-291036878931";

        // INLINE_UID(0xE831FF31, 0xF2D54301, 0x928EBBEE, 0x25697802)
        public const string IComponent = "E831FF31-F2D5-4301-928E-BBEE25697802";

        // INLINE_UID(0x42043F99, 0xB7DA453C, 0xA569E79D, 0x9AAEC33D)
        public const string IAudioProcessor = "42043F99-B7DA-453C-A569-E79D9AAEC33D";

        // INLINE_UID(0xDCD7BBE3, 0x7742448D, 0xA874AACC, 0x979C759E)
        public const string IEditController = "DCD7BBE3-7742-448D-A874-AACC979C759E";

        // INLINE_UID(0x5BC32507, 0xD06049EA, 0xA6151B52, 0x2B755B29)
        public const string IPlugView = "5BC32507-D060-49EA-A615-1B522B755B29";

        // INLINE_UID(0x58E595CC, 0xDB2D4969, 0x8B6AAF8C, 0x36A664E5)
        public const string IHostApplication = "58E595CC-DB2D-4969-8B6A-AF8C36A664E5";

        // INLINE_UID(0xC3BF6EA2, 0x30994752, 0x9B6BF990, 0x1EE33E9B)
        public const string IBStream = "C3BF6EA2-3099-4752-9B6B-F9901EE33E9B";

        // INLINE_UID(0x3A2C4214, 0x346349FE, 0xB2C4F397, 0xB9695A44)
        public const string IEventList = "3A2C4214-3463-49FE-B2C4-F397B9695A44";

        // DECLARE_CLASS_IID(IParameterChanges, 0xA4779663, 0x0BB64A56, 0xB44384A8, 0x466FEB9D)
        public const string IParameterChanges = "A4779663-0BB6-4A56-B443-84A8466FEB9D";

        // DECLARE_CLASS_IID(IParamValueQueue, 0x01263A18, 0xED074F6F, 0x98C9D356, 0x4686F9BA)
        public const string IParamValueQueue = "01263A18-ED07-4F6F-98C9-D3564686F9BA";

        // DECLARE_CLASS_IID(IMidiMapping, 0xDF0FF9F7, 0x49B74669, 0xB63AB732, 0x7ADBF5E5) — CC to parameter routing
        public const string IMidiMapping = "DF0FF9F7-49B7-4669-B63A-B7327ADBF5E5";

        // DECLARE_CLASS_IID(IConnectionPoint, 0x70A4156F, 0x6E6E4026, 0x989148BF, 0xAA60D8D1) — component <-> controller
        public const string IConnectionPoint = "70A4156F-6E6E-4026-9891-48BFAA60D8D1";

        // Kind strings (Vst::PClassInfo.category) — VST3 uses these as string identifiers, not GUIDs.
        public const string kVstAudioEffectClass = "Audio Module Class";

        // Category sub-strings we look for in PClassInfo2.subCategories
        public const string kFxSubCategory = "Fx";
        public const string kInstrumentSubCategory = "Instrument";

        // Steinberg::Vst::ViewType::kEditor - ANSI string "editor"
        public const string ViewTypeEditor = "editor";

        // Platform type strings for IPlugView.attached
        public const string kPlatformTypeHWND = "HWND";
    }
}
