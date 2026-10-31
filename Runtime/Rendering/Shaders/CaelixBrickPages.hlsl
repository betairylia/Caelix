#ifndef CAELIX_BRICK_PAGES_INCLUDED
#define CAELIX_BRICK_PAGES_INCLUDED

// Brick pool pages. One GraphicsBuffer cannot exceed SystemInfo.maxGraphicsBufferSize (~3.9 GB),
// and a large scene holds more brick records than that, so the pool is split into up to sixteen
// buffers and every render group names the page its bricks live in. The page comes from the
// group's instance record, and every brick load below switches on it. Must match
// CaelixBrickPool.MaxNamedPages.
#define CAELIX_BRICK_PAGES 16
ByteAddressBuffer g_bricks0;
ByteAddressBuffer g_bricks1;
ByteAddressBuffer g_bricks2;
ByteAddressBuffer g_bricks3;
ByteAddressBuffer g_bricks4;
ByteAddressBuffer g_bricks5;
ByteAddressBuffer g_bricks6;
ByteAddressBuffer g_bricks7;
ByteAddressBuffer g_bricks8;
ByteAddressBuffer g_bricks9;
ByteAddressBuffer g_bricks10;
ByteAddressBuffer g_bricks11;
ByteAddressBuffer g_bricks12;
ByteAddressBuffer g_bricks13;
ByteAddressBuffer g_bricks14;
ByteAddressBuffer g_bricks15;
static uint _CaelixBrickPage;

uint CaelixBrickPageLoad(uint byteAddress)
{
    switch (_CaelixBrickPage)
    {
        case 0u: return g_bricks0.Load(byteAddress);
        case 1u: return g_bricks1.Load(byteAddress);
        case 2u: return g_bricks2.Load(byteAddress);
        case 3u: return g_bricks3.Load(byteAddress);
        case 4u: return g_bricks4.Load(byteAddress);
        case 5u: return g_bricks5.Load(byteAddress);
        case 6u: return g_bricks6.Load(byteAddress);
        case 7u: return g_bricks7.Load(byteAddress);
        case 8u: return g_bricks8.Load(byteAddress);
        case 9u: return g_bricks9.Load(byteAddress);
        case 10u: return g_bricks10.Load(byteAddress);
        case 11u: return g_bricks11.Load(byteAddress);
        case 12u: return g_bricks12.Load(byteAddress);
        case 13u: return g_bricks13.Load(byteAddress);
        case 14u: return g_bricks14.Load(byteAddress);
        default: return g_bricks15.Load(byteAddress);
    }
}

uint2 CaelixBrickPageLoad2(uint byteAddress)
{
    switch (_CaelixBrickPage)
    {
        case 0u: return g_bricks0.Load2(byteAddress);
        case 1u: return g_bricks1.Load2(byteAddress);
        case 2u: return g_bricks2.Load2(byteAddress);
        case 3u: return g_bricks3.Load2(byteAddress);
        case 4u: return g_bricks4.Load2(byteAddress);
        case 5u: return g_bricks5.Load2(byteAddress);
        case 6u: return g_bricks6.Load2(byteAddress);
        case 7u: return g_bricks7.Load2(byteAddress);
        case 8u: return g_bricks8.Load2(byteAddress);
        case 9u: return g_bricks9.Load2(byteAddress);
        case 10u: return g_bricks10.Load2(byteAddress);
        case 11u: return g_bricks11.Load2(byteAddress);
        case 12u: return g_bricks12.Load2(byteAddress);
        case 13u: return g_bricks13.Load2(byteAddress);
        case 14u: return g_bricks14.Load2(byteAddress);
        default: return g_bricks15.Load2(byteAddress);
    }
}

uint64_t CaelixBrickPageLoad64(uint byteAddress)
{
    switch (_CaelixBrickPage)
    {
        case 0u: return g_bricks0.Load<uint64_t>(byteAddress);
        case 1u: return g_bricks1.Load<uint64_t>(byteAddress);
        case 2u: return g_bricks2.Load<uint64_t>(byteAddress);
        case 3u: return g_bricks3.Load<uint64_t>(byteAddress);
        case 4u: return g_bricks4.Load<uint64_t>(byteAddress);
        case 5u: return g_bricks5.Load<uint64_t>(byteAddress);
        case 6u: return g_bricks6.Load<uint64_t>(byteAddress);
        case 7u: return g_bricks7.Load<uint64_t>(byteAddress);
        case 8u: return g_bricks8.Load<uint64_t>(byteAddress);
        case 9u: return g_bricks9.Load<uint64_t>(byteAddress);
        case 10u: return g_bricks10.Load<uint64_t>(byteAddress);
        case 11u: return g_bricks11.Load<uint64_t>(byteAddress);
        case 12u: return g_bricks12.Load<uint64_t>(byteAddress);
        case 13u: return g_bricks13.Load<uint64_t>(byteAddress);
        case 14u: return g_bricks14.Load<uint64_t>(byteAddress);
        default: return g_bricks15.Load<uint64_t>(byteAddress);
    }
}

#define CAELIX_BRICKS_LOAD(byteAddress) CaelixBrickPageLoad(byteAddress)
#define CAELIX_BRICKS_LOAD2(byteAddress) CaelixBrickPageLoad2(byteAddress)
#define CAELIX_BRICKS_LOAD64(byteAddress) CaelixBrickPageLoad64(byteAddress)

#endif
