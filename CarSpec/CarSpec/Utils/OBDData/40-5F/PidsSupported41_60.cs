namespace CarSpec.Utils.OBDData._40_5F
{
    /// <summary>
    /// PID 0140 — support bitmap for Mode 01 PIDs 0x41..0x60.
    /// </summary>
    public sealed class PidsSupported41_60 : AbstractPidsSupported
    {
        public override string Pid => "0140";
        protected override int BlockStart => 0x40; // covers 0x41..0x60
    }
}