#include "addrinterface.h"
#include <cstdlib>
#include <cstdio>
#include <vector>
#include <set>
#include <cstdint>
static void* ADDR_API allocate(const ADDR_ALLOCSYSMEM_INPUT* p){ return std::malloc(p->sizeInBytes); }
static ADDR_E_RETURNCODE ADDR_API release(const ADDR_FREESYSMEM_INPUT* p){ std::free(p->pVirtAddr); return ADDR_OK; }
static ADDR_E_RETURNCODE ADDR_API debug(const ADDR_DEBUGPRINT_INPUT*){return ADDR_OK;}
int main(int argc,char** argv){
 unsigned revision=argc>1?std::strtoul(argv[1],nullptr,0):0x28;
 unsigned config=argc>2?std::strtoul(argv[2],nullptr,0):0x244;
 ADDR_CREATE_INPUT ci={}; ci.size=sizeof(ci); ci.chipEngine=0xD;ci.chipFamily=0x8F;ci.chipRevision=revision;
 ci.callbacks={allocate,release,debug};ci.createFlags.fillSizeFields=1;ci.regValue.gbAddrConfig=config;
 ADDR_CREATE_OUTPUT co={};co.size=sizeof(co); auto ret=AddrCreate(&ci,&co);if(ret!=ADDR_OK){fprintf(stderr,"create %d\n",ret);return 1;}
 ADDR2_MIP_INFO mip={}; ADDR2_COMPUTE_SURFACE_INFO_INPUT si={};si.size=sizeof(si);
 si.flags.texture=1;si.flags.unordered=1;si.flags.noMetadata=1;si.flags.needEquation=1;
 si.swizzleMode=ADDR_SW_64KB_Z_X;si.resourceType=ADDR_RSRC_TEX_2D;si.format=ADDR_FMT_8;si.bpp=8;
 si.width=1;si.height=1;si.numSlices=1;si.numMipLevels=1;si.numSamples=1;si.numFrags=1;
 ADDR2_COMPUTE_SURFACE_INFO_OUTPUT so={};so.size=sizeof(so);so.pMipInfo=&mip;
 ret=Addr2ComputeSurfaceInfo(co.hLib,&si,&so);if(ret!=ADDR_OK){fprintf(stderr,"info %d\n",ret);AddrDestroy(co.hLib);return 2;}
 fprintf(stderr,"rev=%u config=0x%x swizzle=%u resource=%u bpp=%u block=%ux%ux%u pitch=%u height=%u slices=%u surf=%llu slice=%llu align=%u mipoff=%llu macoff=%llu tail=%u equation=%u\n",revision,config,si.swizzleMode,si.resourceType,so.bpp,so.blockWidth,so.blockHeight,so.blockSlices,so.pitch,so.height,so.numSlices,(unsigned long long)so.surfSize,(unsigned long long)so.sliceSize,so.baseAlign,(unsigned long long)mip.offset,(unsigned long long)mip.macroBlockOffset,mip.mipTailOffset,so.equationIndex);
 if(so.equationIndex!=ADDR_INVALID_EQUATION_INDEX){auto& eq=co.pEquationTable[so.equationIndex];fprintf(stderr,"equation bits=%u components=%u stacked=%u\n",eq.numBits,eq.numBitComponents,eq.stackedDepthSlices);for(unsigned b=0;b<eq.numBits;b++){fprintf(stderr,"bit%u:",b);for(unsigned c=0;c<eq.numBitComponents;c++){auto v=eq.comps[c][b];if(v.valid)fprintf(stderr," %c%u","xyzs"[v.channel],v.index);}fprintf(stderr,"\n");}}
 std::set<uint64_t> seen;std::vector<uint64_t> baseline;unsigned xorChanges=0;
 for(unsigned bank: {0u}) {unsigned i=0;for(unsigned z=0;z<1;z++)for(unsigned y=0;y<1;y++)for(unsigned x=0;x<1;x++,i++){
 ADDR2_COMPUTE_SURFACE_ADDRFROMCOORD_INPUT ai={};ai.size=sizeof(ai);ai.x=x;ai.y=y;ai.slice=z;ai.flags=si.flags;ai.swizzleMode=si.swizzleMode;ai.resourceType=si.resourceType;ai.bpp=si.bpp;ai.unalignedWidth=1;ai.unalignedHeight=1;ai.numSlices=1;ai.numMipLevels=1;ai.numSamples=1;ai.numFrags=1;ai.pipeBankXor=bank;
 ADDR2_COMPUTE_SURFACE_ADDRFROMCOORD_OUTPUT ao={};ao.size=sizeof(ao);ret=Addr2ComputeSurfaceAddrFromCoord(co.hLib,&ai,&ao);
 if(ret!=ADDR_OK || ao.bitPosition || ao.addr%1 || ao.addr+1>so.surfSize){fprintf(stderr,"addr fail %d %u,%u,%u\n",ret,x,y,z);AddrDestroy(co.hLib);return 3;}
 if(bank==0){if(!seen.insert(ao.addr).second){fprintf(stderr,"duplicate\n");return 4;}baseline.push_back(ao.addr);printf("%u,%u,%u,%llu\n",x,y,z,(unsigned long long)ao.addr);}else if(ao.addr!=baseline[i])xorChanges++;
 }}
 fprintf(stderr,"unique=%zu min=%llu max=%llu xorChanges=%u\n",seen.size(),(unsigned long long)*seen.begin(),(unsigned long long)*seen.rbegin(),xorChanges);
 AddrDestroy(co.hLib);return xorChanges?5:0;
}
