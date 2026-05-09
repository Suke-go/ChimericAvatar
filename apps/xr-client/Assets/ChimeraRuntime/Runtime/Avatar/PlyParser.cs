using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Chimera.Runtime
{
    public enum PlyFormat
    {
        Ascii,
        BinaryLittleEndian,
        BinaryBigEndian,
    }

    public class PlyProperty
    {
        public string Name;
        public string Type;            // for scalar properties
        public bool IsList;
        public string ListCountType;   // for list properties
        public string ListItemType;    // for list properties
    }

    public class PlyElement
    {
        public string Name;
        public int Count;
        public List<PlyProperty> Properties = new List<PlyProperty>();
    }

    public class PlyHeader
    {
        public PlyFormat Format;
        public List<PlyElement> Elements = new List<PlyElement>();
        public int HeaderByteLength;

        public PlyElement FindElement(string name)
        {
            foreach (var element in Elements)
            {
                if (element.Name == name) return element;
            }
            return null;
        }
    }

    public static class PlyParser
    {
        public static PlyHeader ParseHeader(byte[] data)
        {
            if (data == null || data.Length < 3) throw new InvalidDataException("PLY data too short");
            var header = new PlyHeader();
            int pos = 0;
            int line = 0;
            PlyElement current = null;
            while (pos < data.Length)
            {
                int eol = FindNewline(data, pos);
                if (eol < 0) throw new InvalidDataException("PLY header is not newline terminated");
                int len = eol - pos;
                if (len > 0 && data[eol - 1] == (byte)'\r') len--;
                string text = Encoding.ASCII.GetString(data, pos, len).Trim();
                pos = eol + 1;
                line++;

                if (line == 1)
                {
                    if (text != "ply") throw new InvalidDataException("PLY missing 'ply' magic");
                    continue;
                }
                if (text.Length == 0 || text.StartsWith("comment ") || text.StartsWith("obj_info "))
                {
                    continue;
                }
                if (text.StartsWith("format "))
                {
                    string[] parts = text.Split(' ');
                    header.Format = parts[1] switch
                    {
                        "ascii" => PlyFormat.Ascii,
                        "binary_little_endian" => PlyFormat.BinaryLittleEndian,
                        "binary_big_endian" => PlyFormat.BinaryBigEndian,
                        _ => throw new InvalidDataException($"unknown PLY format {parts[1]}"),
                    };
                }
                else if (text.StartsWith("element "))
                {
                    string[] parts = text.Split(' ');
                    current = new PlyElement { Name = parts[1], Count = int.Parse(parts[2]) };
                    header.Elements.Add(current);
                }
                else if (text.StartsWith("property "))
                {
                    if (current == null) throw new InvalidDataException("property declared before any element");
                    string[] parts = text.Split(' ');
                    if (parts[1] == "list")
                    {
                        current.Properties.Add(new PlyProperty
                        {
                            IsList = true,
                            ListCountType = parts[2],
                            ListItemType = parts[3],
                            Name = parts[4],
                        });
                    }
                    else
                    {
                        current.Properties.Add(new PlyProperty { Type = parts[1], Name = parts[2] });
                    }
                }
                else if (text == "end_header")
                {
                    header.HeaderByteLength = pos;
                    return header;
                }
            }
            throw new InvalidDataException("PLY header missing end_header");
        }

        public static int ScalarSize(string type)
        {
            return type switch
            {
                "char" or "uchar" or "int8" or "uint8" => 1,
                "short" or "ushort" or "int16" or "uint16" => 2,
                "int" or "uint" or "int32" or "uint32" or "float" or "float32" => 4,
                "double" or "float64" => 8,
                _ => throw new InvalidDataException($"unknown PLY scalar type: {type}"),
            };
        }

        public static int VertexStride(PlyElement element)
        {
            int total = 0;
            foreach (var p in element.Properties)
            {
                if (p.IsList) throw new InvalidDataException("list property in fixed-stride element");
                total += ScalarSize(p.Type);
            }
            return total;
        }

        private static int FindNewline(byte[] data, int from)
        {
            for (int i = from; i < data.Length; i++)
            {
                if (data[i] == (byte)'\n') return i;
            }
            return -1;
        }
    }
}
