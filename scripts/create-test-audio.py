"""Create a quiet, original test tone using only Python's standard library."""
import math
import pathlib
import struct
import sys
import wave

output = pathlib.Path(sys.argv[1])
output.parent.mkdir(parents=True, exist_ok=True)
sample_rate = 22050
seconds = 12
with wave.open(str(output), "wb") as audio:
    audio.setnchannels(1)
    audio.setsampwidth(2)
    audio.setframerate(sample_rate)
    samples = bytearray()
    for frame in range(sample_rate * seconds):
        time = frame / sample_rate
        envelope = min(1.0, time / 0.1, (seconds - time) / 0.1)
        value = int(32767 * 0.02 * envelope * math.sin(2 * math.pi * 440 * time))
        samples.extend(struct.pack("<h", value))
    audio.writeframes(samples)
print(f"Created {output}: {seconds}s, mono PCM16, {sample_rate} Hz, quiet 440 Hz test tone")
