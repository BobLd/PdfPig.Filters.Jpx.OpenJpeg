/*
 * Based on PdfLib.BitStream.cs
 *
 * This class is contributed to the Public Domain.
 * Use it at your own risk.
 */

namespace OpenJpeg.Internal;

/// <remarks>
/// C# impl note.
///
/// This bitwriter writes to a byte buffer, which is then flushed
/// out to a stream as it fills up.
/// </remarks>
internal sealed class WBIO
{
    //Since BufferCIO does its own buffering, I've reduced this
    //buffer from 256 to 4. An alternative is to simply drop this
    //buffer.
    private readonly byte[] _buf = new byte[4];
    private int _buf_pos;
    private byte _unfinished_byte;
    private int _u_pos = 8;
    private readonly BufferCIO _target;

    /// <summary>
    /// BIO will not write beyond this length
    /// </summary>
    private readonly int _length;

    internal WBIO(BufferCIO target, int length)
    {
        _target = target;
        _length = length;
    }

    public void Write(uint value, int nbits)
    {
        for (int i = nbits - 1; i >= 0; i--)
        {
            WriteBit((value >> i) & 1);
        }
    }

    public void WriteBit(uint bit)
    {
        //Commits the buffer when full
        if (_u_pos == 0)
        {
            //Skips a bit to avoid 0xFFFF bit patterns
            _u_pos = (_unfinished_byte == 0xFF) ? 7 : 8;

            //Does not write beyond the length
            if (Written >= _length)
            {
                //Small subtlety: "Flush" will return false
                //since _u_pos is now "7" (While no more
                //writing will be done, as all writes end up
                //here)
                _unfinished_byte = 0;
                return;
            }

            //Writes out the bytes
            _buf[_buf_pos++] = _unfinished_byte;
            Written++;
            if (_buf_pos == _buf.Length)
            {
                _target.Write(_buf, 0, _buf_pos);
                _buf_pos = 0;
            }
            _unfinished_byte = 0;
        }

        //Appends a bit
        _unfinished_byte |= (byte)(bit << --_u_pos);
    }

    public void WriteBit(bool bit) { WriteBit(bit ? 1u : 0); }

    /// <summary>
    /// Empties the buffers
    ///
    /// Data is always byte aligned after flushing.
    /// </summary>
    /// <returns>False if not all data could be written out.</returns>
    public bool Flush()
    {
        bool write_null = false;
        if (_u_pos < 8)
        {
            if (Written >= _length) return false;
            _buf[_buf_pos++] = _unfinished_byte;
            write_null = _unfinished_byte == 0xFF;
            Written++;
        }
        _u_pos = 8; _unfinished_byte = 0;
        if (_buf_pos > 0)
        {
            _target.Write(_buf, 0, _buf_pos);
            _buf_pos = 0;
        }

        //Writes out an extra zero when the data stops
        //with 0xFF
        if (write_null)
        {
            if (Written >= _length) return false;
            _target.WriteByte(0);
            Written++;
        }

        return true;
    }

    /// <summary>
    /// How many bytes have been written to the buffer or file stream
    /// </summary>
    public int Written { get; private set; }
}
