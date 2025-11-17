namespace CarSpec.Utils.OBDData._20_3F
{
    /// <summary>
    /// PID 0120 — support bitmap for Mode 01 PIDs 0x21..0x40.
    /// </summary>
    public sealed class PidsSupported21_40 : AbstractPidsSupported
    {
        // Full PID this parser responds to:
        public override string Pid => "0120";

        // 0x20 indicates this 32-bit word covers absolute PIDs 0x21..0x40
        protected override int BlockStart => 0x20;
    }
}