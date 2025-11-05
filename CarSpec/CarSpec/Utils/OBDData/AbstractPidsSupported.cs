using System;
using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>
    /// Base for Mode 01 PID-support bitmaps (e.g., 0100, 0120, 0140, ...).
    /// Parses the 32-bit support word and exposes helpers.
    /// </summary>
    public abstract class AbstractPidsSupported : AbstractObdData
    {
        /// <summary>Block start (0x00, 0x20, 0x40, 0x60, 0x80).</summary>
        protected abstract int BlockStart { get; } // e.g., 0x00 for 0100, 0x20 for 0120, etc.

        /// <summary>Expected response signature, e.g. "4100", "4120".</summary>
        protected string Signature => "41" + BlockStart.ToString("X2");

        /// <summary>Raw 32-bit support bitmap (MSB = PID+1 at BlockStart+1).</summary>
        public uint Bitmap { get; private set; }

        /// <summary>Hex of the bitmap as read (8 hex chars) for logging/debug.</summary>
        public string BitmapHex { get; private set; } = "00000000";

        /// <summary>
        /// True if this support word can answer for the provided full PID (e.g., "010C").
        /// </summary>
        public bool CanAnswer(string pidHex)
        {
            if (string.IsNullOrWhiteSpace(pidHex) || pidHex.Length != 4) return false;
            if (!pidHex.StartsWith("01", StringComparison.OrdinalIgnoreCase)) return false;

            var absPid = Convert.ToInt32(pidHex.Substring(2), 16); // 0x01..0xA0
            return absPid >= (BlockStart + 0x01) && absPid <= (BlockStart + 0x20);
        }

        /// <summary>
        /// Returns whether this bitmap indicates support for the given full PID (e.g., "010C").
        /// </summary>
        public bool SupportsPid(string pidHex)
        {
            if (!CanAnswer(pidHex)) return false;
            var absPid = Convert.ToInt32(pidHex.Substring(2), 16);
            var withinBlock = absPid - BlockStart; // 1..32
            int bit = 32 - withinBlock;            // MSB -> PID BlockStart+1
            return (Bitmap & (1u << bit)) != 0;
        }

        public override void Parse(string rawResponse)
        {
            Bitmap = 0;
            BitmapHex = "00000000";

            var clean = CleanHex(rawResponse);
            var idx = clean.IndexOf(Signature, StringComparison.Ordinal);
            if (idx < 0) return;

            // After "41xx", the next 8 hex chars are the 32-bit bitmap
            var start = idx + Signature.Length;
            if (clean.Length < start + 8) return;

            var hex = clean.Substring(start, 8);
            if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var map))
            {
                Bitmap = map;
                BitmapHex = hex.ToUpperInvariant();
            }
        }

        /// <summary>
        /// Support map doesn’t directly populate CarData — no-op here.
        /// </summary>
        public override void ApplyTo(CarData target) { /* no-op */ }
    }
}