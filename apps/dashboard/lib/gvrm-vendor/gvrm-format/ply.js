// Copyright (c) 2025 naruya
// Licensed under the MIT License. See LICENSE file in the project root for full license information.


export class PLYParser {
  constructor() {
    this.header = null;
    this.vertexCount = 0;
    this.properties = [];
    this.propertyTypes = new Map([
      ['char', 1], ['uchar', 1],
      ['int8', 1], ['uint8', 1],
      ['short', 2], ['ushort', 2],
      ['int16', 2], ['uint16', 2],
      ['int', 4], ['uint', 4],
      ['int32', 4], ['uint32', 4],
      ['float', 4], ['float32', 4],
      ['double', 8], ['float64', 8]
    ]);
  }

  async parsePLY(url, showProgress) {

    let totalLength;

    if (!url.endsWith('.ply')) {
      showProgress = false;
    }

    if (showProgress) {
      try {
        const headResponse = await fetch(url, { method: 'HEAD' });
        totalLength = Number(headResponse.headers.get('content-length'));
      } catch (error) {
        const response = await fetch(url);
        totalLength = Number(response.headers.get('content-length'));
      }
    } else {
      const response = await fetch(url);
      totalLength = Number(response.headers.get('content-length'));
    }

    const response = await fetch(url);
    const reader = response.body.getReader();
    const chunks = [];

    let loadedLength = 0;

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      chunks.push(value);
      loadedLength += value.length;

      const loaddisplay = document.getElementById('loaddisplay');
      if (loaddisplay) {
        if (totalLength) {
          const progress = (loadedLength / totalLength) * 100;
          loaddisplay.innerHTML = `${progress.toFixed(1)}% (1/2)`;
        } else {
          loaddisplay.innerHTML = `${loadedLength} bytes loaded (1/2)`;
        }
      }
    }
    const loaddisplay = document.getElementById('loaddisplay');
    if (loaddisplay) {
      loaddisplay.innerHTML = `${(100).toFixed(1)}% (1/2)`;
    }

    const arrayBuffer = new ArrayBuffer(loadedLength);
    const uint8View = new Uint8Array(arrayBuffer);

    let offset = 0;
    for (const chunk of chunks) {
      uint8View.set(chunk, offset);
      offset += chunk.length;
    }

    const data = new DataView(arrayBuffer);
    offset = 0;

    let headerText = '';
    while (true) {
      const byte = data.getUint8(offset++);
      headerText += String.fromCharCode(byte);
      if (headerText.includes('end_header\n')) break;
    }

    const headerLines = headerText.split('\n');
    this.header = headerLines.filter(line => line.trim() !== '');
    
    let format = 'binary_little_endian';
    for (const line of this.header) {
      if (line.startsWith('format')) {
        format = line.split(' ')[1];
      } else if (line.startsWith('element vertex')) {
        this.vertexCount = parseInt(line.split(' ')[2]);
      } else if (line.startsWith('property')) {
        const parts = line.split(' ');
        this.properties.push({
          type: parts[1],
          name: parts[2]
        });
      }
    }

    const vertexSize = this.properties.reduce((size, prop) => {
      return size + this.propertyTypes.get(prop.type);
    }, 0);

    const vertices = [];
    const verticesRawData = new Uint8Array(arrayBuffer.slice(offset));

    for (let i = 0; i < this.vertexCount; i++) {
      const vertex = {
        rawData: verticesRawData.slice(i * vertexSize, (i + 1) * vertexSize)
      };
      
      let propertyOffset = 0;
      for (const prop of this.properties) {
        const size = this.propertyTypes.get(prop.type);
        let value;
        
        switch (prop.type) {
          case 'float':
            value = data.getFloat32(offset + propertyOffset, true);
            break;
        }
        
        vertex[prop.name] = value;
        propertyOffset += size;
      }
      
      vertices.push(vertex);
      offset += vertexSize;

      if (i % 10000 === 0) {
        const progress = (i / this.vertexCount) * 100;
        const loaddisplay = document.getElementById('loaddisplay');
        if (loaddisplay) {
          await new Promise(resolve => {
            requestAnimationFrame(() => {
              loaddisplay.innerHTML = `${progress.toFixed(1)}% (2/2)`;
              resolve();
            });
          });
        }
      }
    }
    const loaddisplayFinal = document.getElementById('loaddisplay');
    if (loaddisplayFinal) {
      loaddisplayFinal.innerHTML = `${(100).toFixed(1)}% (2/2)`;
    }

    return {
      header: this.header,
      vertices: vertices,
      vertexCount: this.vertexCount,
      vertexSize: vertexSize
    };
  }

  createPLYFile(header, vertices, vertexSize) {
    const headerStr = header.join('\n') + '\n';
    const encoder = new TextEncoder();
    const headerArray = encoder.encode(headerStr);

    const verticesArray = new Uint8Array(vertices.length * vertexSize);
    vertices.forEach((vertex, index) => {
      verticesArray.set(vertex.rawData, index * vertexSize);
    });

    const finalArray = new Uint8Array(headerArray.length + verticesArray.length);
    finalArray.set(headerArray, 0);
    finalArray.set(verticesArray, headerArray.length);

    return finalArray;
  }

  async splitPLY(plyUrl, sceneSplatIndices) {
    const direct = await this.splitPLYBinaryLossless(plyUrl, sceneSplatIndices);
    if (direct) return direct;

    const plyData = await this.parsePLY(plyUrl, false);

    const createModifiedHeader = (vertexCount) => {
      return plyData.header.map(line => {
        if (line.startsWith('element vertex')) {
          return `element vertex ${vertexCount}`;
        }
        return line;
      });
    };

    const sceneUrls = [];
    for (const [sceneIndex, indices] of Object.entries(sceneSplatIndices)) {
      const sceneVertices = indices.map(index => plyData.vertices[index]);

      const scenePlyData = this.createPLYFile(
        createModifiedHeader(sceneVertices.length),
        sceneVertices,
        plyData.vertexSize
      );

      const blob = new Blob([scenePlyData], { type: 'application/octet-stream' });
      sceneUrls.push(URL.createObjectURL(blob));
    }

    return sceneUrls;
  }

  async splitPLYBinaryLossless(plyUrl, sceneSplatIndices) {
    const response = await fetch(plyUrl);
    const arrayBuffer = await response.arrayBuffer();
    const data = new Uint8Array(arrayBuffer);
    const headerEnd = this.findHeaderEnd(data);
    if (headerEnd < 0) return null;

    const decoder = new TextDecoder('ascii');
    const headerText = decoder.decode(data.subarray(0, headerEnd));
    const headerLines = headerText.split(/\r?\n/).filter(line => line.trim() !== '');

    let format = null;
    let vertexCount = null;
    let vertexSize = 0;
    let inVertex = false;
    for (const line of headerLines) {
      const parts = line.trim().split(/\s+/);
      if (parts[0] === 'format') {
        format = parts[1];
        if (format !== 'binary_little_endian' && format !== 'binary_big_endian') {
          return null;
        }
      } else if (parts[0] === 'element') {
        inVertex = parts[1] === 'vertex';
        if (inVertex) {
          vertexCount = Number(parts[2]);
          vertexSize = 0;
        }
      } else if (inVertex && parts[0] === 'property') {
        if (parts[1] === 'list') return null;
        const size = this.propertyTypes.get(parts[1]);
        if (!size) return null;
        vertexSize += size;
      }
    }

    if (!format || !Number.isFinite(vertexCount) || vertexSize <= 0) return null;
    const vertexBytesStart = headerEnd;
    const vertexBytesEnd = vertexBytesStart + vertexCount * vertexSize;
    if (data.byteLength < vertexBytesEnd) return null;

    const encoder = new TextEncoder();
    const createModifiedHeader = (count) => {
      const lines = headerLines.map(line =>
        line.startsWith('element vertex') ? `element vertex ${count}` : line
      );
      return encoder.encode(lines.join('\n') + '\n');
    };

    const sceneUrls = [];
    for (const indices of Object.values(sceneSplatIndices)) {
      const headerArray = createModifiedHeader(indices.length);
      const verticesArray = new Uint8Array(indices.length * vertexSize);
      for (let outIndex = 0; outIndex < indices.length; outIndex++) {
        const sourceIndex = indices[outIndex];
        const src = vertexBytesStart + sourceIndex * vertexSize;
        const dst = outIndex * vertexSize;
        verticesArray.set(data.subarray(src, src + vertexSize), dst);
      }

      const finalArray = new Uint8Array(headerArray.length + verticesArray.length);
      finalArray.set(headerArray, 0);
      finalArray.set(verticesArray, headerArray.length);
      const blob = new Blob([finalArray], { type: 'application/octet-stream' });
      sceneUrls.push(URL.createObjectURL(blob));
    }

    return sceneUrls;
  }

  findHeaderEnd(data) {
    const marker = new TextEncoder().encode('end_header\n');
    for (let i = 0; i <= data.length - marker.length; i++) {
      let ok = true;
      for (let j = 0; j < marker.length; j++) {
        if (data[i + j] !== marker[j]) {
          ok = false;
          break;
        }
      }
      if (ok) return i + marker.length;
    }

    const crlfMarker = new TextEncoder().encode('end_header\r\n');
    for (let i = 0; i <= data.length - crlfMarker.length; i++) {
      let ok = true;
      for (let j = 0; j < crlfMarker.length; j++) {
        if (data[i + j] !== crlfMarker[j]) {
          ok = false;
          break;
        }
      }
      if (ok) return i + crlfMarker.length;
    }
    return -1;
  }
}
