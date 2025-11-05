namespace CarSpec.Utils.OBDData._00_1F
{
    /// <summary>
    /// PID 0100 — support bitmap for Mode 01 PIDs 0x01..0x20.
    /// </summary>
    public sealed class PidsSupported01_20 : AbstractPidsSupported
    {
        // Full PID this parser responds to:
        public override string Pid => "0100";

        // 0x00 indicates this 32-bit word covers absolute PIDs 0x01..0x20
        protected override int BlockStart => 0x00;
    }
}