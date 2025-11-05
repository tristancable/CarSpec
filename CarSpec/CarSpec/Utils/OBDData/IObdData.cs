using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>
    /// Contract for one Mode 01 PID parser that can populate CarData.
    /// </summary>
    public interface IObdData
    {
        /// <summary>4-hex PID code including mode (e.g., "010C").</summary>
        string Pid { get; }

        /// <summary>Parse the raw adapter response (may contain headers/spaces/newlines).</summary>
        void Parse(string rawResponse);

        /// <summary>Apply the parsed value(s) onto an existing CarData snapshot.</summary>
        void ApplyTo(CarData target);
    }
}