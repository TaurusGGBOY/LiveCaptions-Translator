// Preserve every original bundled entry; replace only the main application DLL.
const fs = require('fs'), zlib = require('zlib'), crypto = require('crypto');
const [mode, input, dllPath, output] = process.argv.slice(2);
const original = fs.readFileSync(input);
const signature = Buffer.from('8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae', 'hex');
const sig = original.indexOf(signature);
if (sig < 8 || original.indexOf(signature, sig + 1) >= 0) throw Error('Invalid .NET bundle');
let pos = Number(original.readBigInt64LE(sig - 8));
const u32 = () => { const v=original.readUInt32LE(pos); pos+=4; return v; };
const i64 = () => { const v=original.readBigInt64LE(pos); pos+=8; return v; };
const str = () => { let n=0, shift=0, b; do { b=original[pos++]; n|=(b&127)<<shift; shift+=7; } while(b&128); const s=original.subarray(pos,pos+n).toString(); pos+=n; return s; };
const major=u32(), minor=u32(), count=u32(), id=str();
if (major !== 6) throw Error('Only .NET bundle v6 is supported');
const headerValues = [i64(),i64(),i64(),i64(),i64()];
const entries=[];
for(let i=0;i<count;i++) entries.push({offset:i64(), size:i64(), compressed:i64(), type:original[pos++], path:str()});
const entry=entries.find(e=>e.path==='LiveCaptionsTranslator.dll');
if (!entry) throw Error('Main DLL missing');
if (mode==='extract') {
  let bytes=original.subarray(Number(entry.offset),Number(entry.offset+(entry.compressed||entry.size)));
  if(entry.compressed) bytes=zlib.inflateRawSync(bytes);
  fs.writeFileSync(dllPath,bytes);
} else if (mode==='replace') {
  const patched=fs.readFileSync(dllPath);
  entry.offset=BigInt(original.length); entry.size=BigInt(patched.length); entry.compressed=0n;
  const chunks=[];
  const w32=n=>{const b=Buffer.alloc(4);b.writeUInt32LE(n);chunks.push(b);};
  const w64=n=>{const b=Buffer.alloc(8);b.writeBigInt64LE(n);chunks.push(b);};
  const ws=s=>{const b=Buffer.from(s);let n=b.length;const l=[];while(n>=128){l.push((n&127)|128);n>>>=7;}l.push(n);chunks.push(Buffer.from(l),b);};
  w32(major);w32(minor);w32(count);
  // Unique extraction directory prevents Windows reusing a cached old DLL.
  ws(crypto.randomBytes(12).toString('hex'));
  headerValues.forEach(w64);
  for(const e of entries){w64(e.offset);w64(e.size);w64(e.compressed);chunks.push(Buffer.from([e.type]));ws(e.path);}
  original.writeBigInt64LE(BigInt(original.length+patched.length),sig-8);
  fs.writeFileSync(output,Buffer.concat([original,patched,...chunks]));
} else throw Error('Usage: node bundle.cjs extract|replace original.exe main.dll [output.exe]');
console.log('Bundle entries preserved: '+count+'; operation: '+mode);
