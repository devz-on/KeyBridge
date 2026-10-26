// Bounded decoder for the definite-length CBOR used by WebAuthn attestation objects.
// No tags, floating-point values, indefinite lengths, or executable data are accepted.
export function decodeCborPrefix(input, start = 0) {
  const bytes = input instanceof Uint8Array ? input : new Uint8Array(input);
  if (bytes.length > 65536 || !Number.isInteger(start) || start < 0 || start >= bytes.length) throw new Error('CBOR size or offset is invalid.');
  let offset = start;
  let items = 0;
  const text = new TextDecoder('utf-8', { fatal: true });
  const take = (length) => {
    if (!Number.isSafeInteger(length) || length < 0 || length > bytes.length - offset) throw new Error('Truncated CBOR.');
    const value = bytes.slice(offset, offset + length);
    offset += length;
    return value;
  };
  const length = (additional) => {
    if (additional < 24) return additional;
    if (additional === 24) {
      const value = take(1)[0];
      if (value < 24) throw new Error('Non-minimal CBOR integer.');
      return value;
    }
    if (additional === 25) {
      const value = take(2);
      const number = value[0] * 256 + value[1];
      if (number <= 255) throw new Error('Non-minimal CBOR integer.');
      return number;
    }
    if (additional === 26) {
      const value = take(4);
      const number = new DataView(value.buffer).getUint32(0);
      if (number <= 65535) throw new Error('Non-minimal CBOR integer.');
      return number;
    }
    throw new Error('Unsupported CBOR length.');
  };
  const read = (depth) => {
    if (depth > 16 || ++items > 1024) throw new Error('CBOR nesting or item limit exceeded.');
    const initial = take(1)[0];
    const major = initial >> 5;
    const additional = initial & 31;
    if (major === 7) {
      if (additional === 20) return false;
      if (additional === 21) return true;
      if (additional === 22) return null;
      throw new Error('Unsupported CBOR simple value.');
    }
    const size = length(additional);
    if (major === 0) return size;
    if (major === 1) return -1 - size;
    if (major === 2) return take(size);
    if (major === 3) return text.decode(take(size));
    if ((major === 4 || major === 5) && size > 512) throw new Error('CBOR container limit exceeded.');
    if (major === 4) return Array.from({ length: size }, () => read(depth + 1));
    if (major === 5) {
      const map = new Map();
      for (let i = 0; i < size; i++) {
        const key = read(depth + 1);
        if (typeof key !== 'string' && typeof key !== 'number') throw new Error('Unsupported CBOR map key.');
        if (map.has(key)) throw new Error('Duplicate CBOR map key.');
        map.set(key, read(depth + 1));
      }
      return map;
    }
    throw new Error('Unsupported CBOR major type.');
  };
  return { value: read(0), offset };
}

export function decodeCbor(input) {
  const bytes = input instanceof Uint8Array ? input : new Uint8Array(input);
  const decoded = decodeCborPrefix(bytes);
  if (decoded.offset !== bytes.length) throw new Error('Trailing CBOR data.');
  return decoded.value;
}
