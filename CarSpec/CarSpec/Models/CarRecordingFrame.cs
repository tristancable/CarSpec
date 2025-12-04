using System;

namespace CarSpec.Models
{
    public sealed class CarRecordingFrame
    {
        public long t { get; set; }
        public CarData data { get; set; } = new CarData();
    }
}