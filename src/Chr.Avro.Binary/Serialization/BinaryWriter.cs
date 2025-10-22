namespace Chr.Avro.Serialization
{
    using System;
    using System.Buffers;
#if NET6_0_OR_GREATER
    using System.Buffers.Binary;
#endif
    using System.IO;
    using System.Text;

    /// <summary>
    /// Writes primitive values to binary Avro data.
    /// </summary>
    public sealed class BinaryWriter : IDisposable
    {
        private readonly Stream stream;

        /// <summary>
        /// Initializes a new instance of the <see cref="BinaryWriter" /> class.
        /// </summary>
        /// <param name="stream">
        /// The binary Avro destination.
        /// </param>
        public BinaryWriter(Stream stream)
        {
            this.stream = stream;
        }

        /// <summary>
        /// Frees any resources used by the writer and flushes the <see cref="Stream" />. The
        /// <see cref="Stream" /> is not disposed.
        /// </summary>
        public void Dispose()
        {
            stream.Flush();
        }

        /// <summary>
        /// Writes a Boolean value to the current position and advances the writer.
        /// </summary>
        /// <param name="value">
        /// A <see cref="bool" /> value.
        /// </param>
        public void WriteBoolean(bool value)
        {
            stream.WriteByte(value ? (byte)0x01 : (byte)0x00);
        }

        /// <summary>
        /// Writes variable-length binary data to the current position and advances the writer.
        /// </summary>
        /// <param name="value">
        /// An array of <see cref="byte" />s.
        /// </param>
        public void WriteBytes(byte[] value)
        {
            WriteInteger(value.Length);
            WriteFixed(value);
        }

        /// <summary>
        /// Writes fixed-length binary data to the current position and advances the writer.
        /// </summary>
        /// <param name="value">
        /// An array of <see cref="byte" />s.
        /// </param>
        public void WriteBytes(ReadOnlySpan<byte> value)
        {
            WriteInteger(value.Length);
            WriteFixed(value);
        }

        /// <summary>
        /// Writes a double-precision floating-point number to the current position and advances
        /// the writer.
        /// </summary>
        /// <param name="value">
        /// A <see cref="double" /> value.
        /// </param>
        public void WriteDouble(double value)
        {
#if NET6_0_OR_GREATER
            Span<byte> bytes = stackalloc byte[sizeof(double)];
            BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
#else
            var bytes = BitConverter.GetBytes(value);

            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
#endif

            WriteFixed(bytes);
        }

        /// <summary>
        /// Writes fixed-length binary data to the current position and advances the writer.
        /// </summary>
        /// <param name="value">
        /// An array of <see cref="byte" />s.
        /// </param>
        public void WriteFixed(byte[] value)
        {
            stream.Write(value, 0, value.Length);
        }

        /// <summary>
        /// Writes fixed-length binary data to the current position and advances the writer.
        /// </summary>
        /// <param name="value">
        /// An array of <see cref="byte" />s.
        /// </param>
        public void WriteFixed(ReadOnlySpan<byte> value)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(value.Length);
            try
            {
                value.CopyTo(buffer);
                stream.Write(buffer, 0, value.Length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Writes a variable-length integer to the current position and advances the writer.
        /// </summary>
        /// <param name="value">
        /// An <see cref="int" /> value.
        /// </param>
        public void WriteInteger(int value)
        {
            var encoded = (uint)((value << 1) ^ (value >> 31));
#if NET6_0_OR_GREATER
            Span<byte> buffer = stackalloc byte[5]; // Max 5 bytes for 32-bit varint
            int index = 0;

            do
            {
                var current = encoded & 0x7F;
                encoded >>= 7;

                if (encoded != 0)
                {
                    current |= 0x80U;
                }

                buffer[index++] = (byte)current;
            }
            while (encoded != 0U);

            stream.Write(buffer.Slice(0, index));
#else
            do
            {
                var current = encoded & 0x7FU;
                encoded >>= 7;

                if (encoded != 0)
                {
                    current |= 0x80U;
                }

                stream.WriteByte((byte)current);
            }
            while (encoded != 0U);
#endif
        }

        /// <summary>
        /// Writes a variable-length integer to the current position and advances the writer.
        /// </summary>
        /// <param name="value">
        /// A <see cref="long" /> value.
        /// </param>
        public void WriteInteger(long value)
        {
            var encoded = (ulong)((value << 1) ^ (value >> 63));

#if NET6_0_OR_GREATER
            Span<byte> buffer = stackalloc byte[10]; // Max 10 bytes for 64-bit varint
            int index = 0;

            do
            {
                var current = encoded & 0x7F;
                encoded >>= 7;

                if (encoded != 0)
                {
                    current |= 0x80;
                }

                buffer[index++] = (byte)current;
            }
            while (encoded != 0UL);
            stream.Write(buffer.Slice(0, index));
#else
            do
            {
                var current = encoded & 0x7FUL;
                encoded >>= 7;

                if (encoded != 0)
                {
                    current |= 0x80UL;
                }

                stream.WriteByte((byte)current);
            }
            while (encoded != 0UL);
#endif
        }

        /// <summary>
        /// Writes a double-precision floating point number to the current position and advances
        /// the writer.
        /// </summary>
        /// <param name="value">
        /// A <see cref="float" /> value.
        /// </param>
        public void WriteSingle(float value)
        {
#if NET6_0_OR_GREATER
            Span<byte> bytes = stackalloc byte[sizeof(float)];
            BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
#else
            var bytes = BitConverter.GetBytes(value);

            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
#endif

            WriteFixed(bytes);
        }

        /// <summary>
        /// Writes a UTF-8 string to the current position and advances the writer.
        /// </summary>
        /// <param name="value">
        /// A <see cref="string" /> value.
        /// </param>
        public void WriteString(string value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);

            WriteInteger(byteCount);

#if NET6_0_OR_GREATER
            if (byteCount <= 256)
            {
                Span<byte> buffer = stackalloc byte[256];
                int actualCount = Encoding.UTF8.GetBytes(value, buffer);
                stream.Write(buffer.Slice(0, actualCount));
            }
            else
            {
                byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
                try
                {
                    int actualCount = Encoding.UTF8.GetBytes(value, rented);
                    stream.Write(rented, 0, actualCount);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
#else
            byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                int actualCount = Encoding.UTF8.GetBytes(value, 0, value.Length, rented, 0);
                stream.Write(rented, 0, actualCount);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
#endif
        }
    }
}
