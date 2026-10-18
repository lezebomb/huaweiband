// Read-only APK/DEX index for focused protocol research. Does not read phone app data.
import fs from 'node:fs';
import zlib from 'node:zlib';
import path from 'node:path';
import crypto from 'node:crypto';
const input=process.argv[2], output=process.argv[3];
if(!input || !output)throw Error('Usage: node inspect-health-apk.mjs APK OUTPUT_DIRECTORY');
fs.mkdirSync(output,{recursive:true});
const apk=fs.readFileSync(input);
let end=-1;
for(let i=apk.length-22;i>=Math.max(0,apk.length-65557);i--)if(apk.readUInt32LE(i)===0x06054b50){end=i;break;}
if(end<0)throw Error('ZIP central directory missing');
let cursor=apk.readUInt32LE(end+16), count=apk.readUInt16LE(end+10);
if(count===65535)throw Error('ZIP64 not supported by this focused indexer');
const classes=[], hits=[];
for(let i=0;i<count;i++){
  if(apk.readUInt32LE(cursor)!==0x02014b50)throw Error('Invalid ZIP member');
  const method=apk.readUInt16LE(cursor+10),size=apk.readUInt32LE(cursor+20),nameSize=apk.readUInt16LE(cursor+28),extra=apk.readUInt16LE(cursor+30),comment=apk.readUInt16LE(cursor+32),local=apk.readUInt32LE(cursor+42);
  const name=apk.toString('utf8',cursor+46,cursor+46+nameSize);cursor+=46+nameSize+extra+comment;
  if(!/^classes\d*\.dex$/.test(name))continue;
  const start=local+30+apk.readUInt16LE(local+26)+apk.readUInt16LE(local+28);
  const compressed=apk.subarray(start,start+size), dex=method===0?compressed:zlib.inflateRawSync(compressed);
  if(dex.toString('ascii',0,4)!=='dex\n')continue;
  const stringCount=dex.readUInt32LE(0x38),stringsOffset=dex.readUInt32LE(0x3c),typesOffset=dex.readUInt32LE(0x44),classCount=dex.readUInt32LE(0x60),classOffset=dex.readUInt32LE(0x64);
  const strings=new Array(stringCount);
  function string(index){if(strings[index]!==undefined)return strings[index];let p=dex.readUInt32LE(stringsOffset+index*4);while(dex[p++]&128){}let e=dex.indexOf(0,p);return strings[index]=dex.toString('utf8',p,e);}
  const members=[];
  for(let c=0;c<classCount;c++){
    const type=dex.readUInt32LE(classOffset+c*32), descriptor=string(dex.readUInt32LE(typesOffset+type*4));
    if(/\/wearengine\/|\/wearengineext\//i.test(descriptor))members.push(descriptor);
  }
  if(members.length){fs.writeFileSync(path.join(output,name),dex);for(const descriptor of members)classes.push({dex:name,descriptor});}
  for(let s=0;s<stringCount;s++){
    const value=string(s);
    if(value.length<250 && /CDY-B59|NOTIFICATION_TEMPLATE|NotifyCapability|notifyCapability|wearengine.*notify|wearengine.*permission|BUTTON_ONE_CONTENT_KEY/i.test(value))hits.push({dex:name,value});
  }
}
const report={apkSha256:crypto.createHash('sha256').update(apk).digest('hex'),classes,hits};
fs.writeFileSync(path.join(output,'index.json'),JSON.stringify(report,null,2));
console.log(JSON.stringify({sha256:report.apkSha256,classes:classes.length,dexFiles:[...new Set(classes.map(c=>c.dex))],service:classes.filter(c=>/WearEngineService|AuthService|NotifyService|DeviceService|Capability/i.test(c.descriptor)),hits:hits.slice(0,80)},null,2));
