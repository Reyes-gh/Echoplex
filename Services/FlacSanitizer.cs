namespace Echoplex.Services;

/// <summary>
/// El decodificador FLAC de Windows (Media Foundation) rechaza archivos con bloques de metadatos
/// enormes, típicamente portadas incrustadas de varios MB. Esto construye un flujo virtual con la
/// cabecera sin esos bloques y el audio intacto, sin modificar el archivo original.
/// </summary>
public static class FlacSanitizer
{
    private const int PictureBlock = 6;
    private const int MaxKeptBlock = 256 * 1024;

    /// <summary>Devuelve un flujo saneado, o null si el archivo no necesita cambios.</summary>
    public static Stream? TryOpen(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        try
        {
            var magic = new byte[4];
            if (fs.Read(magic, 0, 4) != 4 || magic[0] != 'f' || magic[1] != 'L' || magic[2] != 'a' || magic[3] != 'C')
            {
                fs.Dispose();
                return null;
            }

            var kept = new List<(byte type, byte[] data)>();
            bool changed = false, last = false;
            var hdr = new byte[4];
            while (!last)
            {
                if (fs.Read(hdr, 0, 4) != 4) { fs.Dispose(); return null; }
                last = (hdr[0] & 0x80) != 0;
                byte type = (byte)(hdr[0] & 0x7F);
                int len = (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
                if (type == PictureBlock || (type != 0 && len > MaxKeptBlock))
                {
                    fs.Seek(len, SeekOrigin.Current);
                    changed = true;
                    continue;
                }
                var data = new byte[len];
                if (fs.Read(data, 0, len) != len) { fs.Dispose(); return null; }
                kept.Add((type, data));
            }

            if (!changed || kept.Count == 0 || kept[0].type != 0)
            {
                fs.Dispose();
                return null;
            }

            var ms = new MemoryStream();
            ms.Write(magic);
            for (int i = 0; i < kept.Count; i++)
            {
                var (type, data) = kept[i];
                ms.WriteByte((byte)((i == kept.Count - 1 ? 0x80 : 0) | type));
                ms.WriteByte((byte)(data.Length >> 16));
                ms.WriteByte((byte)(data.Length >> 8));
                ms.WriteByte((byte)data.Length);
                ms.Write(data);
            }
            return new ConcatStream(ms.ToArray(), fs, fs.Position);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>Flujo de solo lectura: cabecera en memoria + resto del archivo desde un desplazamiento.</summary>
    private sealed class ConcatStream : Stream
    {
        private readonly byte[] _head;
        private readonly FileStream _file;
        private readonly long _fileStart;
        private readonly long _length;
        private long _pos;

        public ConcatStream(byte[] head, FileStream file, long fileStart)
        {
            _head = head;
            _file = file;
            _fileStart = fileStart;
            _length = head.Length + (file.Length - fileStart);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _pos;
            set => _pos = Math.Clamp(value, 0, _length);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (count > 0 && _pos < _length)
            {
                int n;
                if (_pos < _head.Length)
                {
                    n = (int)Math.Min(count, _head.Length - _pos);
                    Buffer.BlockCopy(_head, (int)_pos, buffer, offset, n);
                }
                else
                {
                    _file.Position = _fileStart + (_pos - _head.Length);
                    n = _file.Read(buffer, offset, (int)Math.Min(count, _length - _pos));
                    if (n <= 0) break;
                }
                _pos += n;
                offset += n;
                count -= n;
                total += n;
            }
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                _ => _length + offset,
            };
            return _pos;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _file.Dispose();
            base.Dispose(disposing);
        }
    }
}
