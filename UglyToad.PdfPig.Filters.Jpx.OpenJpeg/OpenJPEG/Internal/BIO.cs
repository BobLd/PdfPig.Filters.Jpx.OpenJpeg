/*
 * Based on PdfLib.BitStream.cs
 *
 * This class is contributed to the Public Domain.
 * Use it at your own risk.
 */

namespace OpenJpeg.Internal;

/// <summary>
/// Bit In/Out
/// </summary>
/// <remarks>
/// Based on PdfLib.BitStream, but this is not a plain
/// bit reader as some bits are automatically skipped
/// </remarks>
internal sealed class BIO
{
    /// <summary>
    /// The data
    /// </summary>
    private readonly byte[] _data;

    /// <summary>
    /// Position in the data stream
    /// </summary>
    private int _data_pos;

    private readonly int _start_pos;

    private readonly int _end_pos;

    /// <summary>
    /// A buffer containing up to 8 bits
    /// </summary>
    /// <remarks>
    /// Biggest read is 7 bits, with one potential exception
    /// that may exceed the 24-bit limit of the org impl.
    /// anyway.
    /// </remarks>
    private int bit_buffer;

    /// <summary>
    /// Encoder: Number of free bits in the buffer
    /// Decoder: Number of bits in the buffer
    /// </summary>
    private int n_buf_bits;

    /// <summary>
    /// Mask for the first bit in the bit buffer
    /// </summary>
    private const byte FIRST_BIT = 0x80;

    internal int Position => _data_pos - _start_pos;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="data">Data to read from</param>
    /// <param name="start_pos">Position to start reading from</param>
    /// <param name="end_pos">Position to stop reading at (exclusive)</param>
    internal BIO(byte[] data, int start_pos, int end_pos)
    {
        _data = data;
        _data_pos = start_pos;
        _start_pos = start_pos;
        _end_pos = end_pos;
    }

    /// <summary>
    /// Can read up to 32 bits out of the bitstream.
    /// </summary>
    /// <param name="n">Number of bits to fetch, max is 32</param>
    /// <param name="zeroAtEnd">
    /// Reads "0" bits beyond the end of the stream instead of throwing
    /// </param>
    /// <returns>The bits in the lower end of a uint</returns>
    /// <remarks>
    /// Special note: 0xff skips the next bit. Because of
    /// this I suspect that there's never reads above 8 bits, so
    /// this function may not be needed.
    /// </remarks>
    internal uint ReadUInt(uint n, bool zeroAtEnd = false)
    {
        uint ret = 0;
        while (n > 8)
        {
            ret = ret << 8 | (uint)Read(8, zeroAtEnd);
            n -= 8;
        }
        return ret << (int)n | (uint)Read((int)n, zeroAtEnd);
    }

    /// <summary>
    /// Reads bits out of the bitstream.
    /// </summary>
    /// <param name="n">Number of bits to fetch, max is 8</param>
    /// <param name="zeroAtEnd">
    /// Reads "0" bits beyond the end of the stream, as the original
    /// impl. does, instead of throwing
    /// </param>
    /// <returns>The bits in the lower end of a int</returns>
    internal int Read(int n, bool zeroAtEnd = false)
    {
        int ret = (bit_buffer & 0xff) >> (8 - n);
        if (n > n_buf_bits)
        {
            n -= n_buf_bits;
            //After a 0xff byte, skips over 1 bit of data
            bool skip = ((bit_buffer << n_buf_bits) & 0xff00) == 0xff00;
            if (_data_pos != _end_pos)
            {
                bit_buffer = _data[_data_pos++];
            }
            else if (zeroAtEnd)
            {
                bit_buffer = 0;
            }
            else
            {
                throw new EndOfStreamException();
            }

            if (skip)
            {
                bit_buffer <<= 1;
                n_buf_bits = 7 - n;
                ret |= (bit_buffer & 0xff) >> (8 - n);
            }
            else
            {
                n_buf_bits = 8 - n;
                ret |= (bit_buffer & 0xff) >> n_buf_bits;
            }
        }
        else
        {
            n_buf_bits -= n;
        }

        bit_buffer <<= n;
        return ret;
    }

    /// <summary>
    /// Reads a single bit out of the bitstream
    /// </summary>
    /// <returns>True if the bit was 1</returns>
    internal bool ReadBool()
    {
        if (n_buf_bits == 0)
        {
            if (_data_pos == _end_pos)
                return false; //<-- Yes, this is what the org impl. does
            bit_buffer |= _data[_data_pos++];
            n_buf_bits = 8;
            if ((bit_buffer & 0xff00) == 0xff00)
            {
                //Skips one bit
                bit_buffer <<= 1;
                n_buf_bits--;
            }
        }
        bool ret = (bit_buffer & FIRST_BIT) == FIRST_BIT;
        bit_buffer <<= 1;
        n_buf_bits--;
        return ret;
    }

    /// <summary>
    /// Aligns the stream to the next byte.
    /// </summary>
    /// <returns>True if an End of Stream</returns>
    /// <remarks>
    /// Don't read into the buffer as there's code
    /// reading the position afterward
    /// </remarks>
    internal bool ByteAlign()
    {
        //Skips over a byte if the buffer contains 0xff
        //C# impl. note: Since I bitshift the buffer, make
        // that 0xff00
        if (((bit_buffer << n_buf_bits) & 0xff00) == 0xff00)
        {
            _data_pos++;
            if (_data_pos == _end_pos)
                return true;
        }
        n_buf_bits = 0;
        return false;
    }
}
