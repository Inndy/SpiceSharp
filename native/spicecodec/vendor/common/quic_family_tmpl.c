/* -*- Mode: C; c-basic-offset: 4; indent-tabs-mode: nil -*- */
/*
   Copyright (C) 2009 Red Hat, Inc.

   This library is free software; you can redistribute it and/or
   modify it under the terms of the GNU Lesser General Public
   License as published by the Free Software Foundation; either
   version 2.1 of the License, or (at your option) any later version.

   This library is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
   Lesser General Public License for more details.

   You should have received a copy of the GNU Lesser General Public
   License along with this library; if not, see <http://www.gnu.org/licenses/>.
*/

/* MODIFIED FILE (LGPL-2.1 section 2b). Changed from upstream spice-common common/quic_family_tmpl.c (commit 58d375e5):
   SpiceSharp, 2026-10-30: with AVX2 (x86-64-v3), update_model for 8 bits per channel updates its eight
     counters and finds the best code with vector instructions.
   Each change is marked SPICESHARP MODIFICATION below and listed in native/VENDORED.md. */
#include <config.h>

#ifdef QUIC_FAMILY_8BPC
#undef QUIC_FAMILY_8BPC
#define FNAME(name) name##_8bpc
#define VNAME(name) name##_8bpc
#define BPC 8
#endif


#ifdef QUIC_FAMILY_5BPC
#undef QUIC_FAMILY_5BPC
#define FNAME(name) name##_5bpc
#define VNAME(name) name##_5bpc
#define BPC 5
#endif

static inline unsigned int FNAME(golomb_code)(const BYTE n, const unsigned int l)
{
    return VNAME(family).golomb_code[n][l];
}

static inline unsigned int FNAME(golomb_code_len)(const BYTE n, const unsigned int l)
{
    return VNAME(family).golomb_code_len[n][l];
}

static void FNAME(golomb_coding)(Encoder *encoder, const BYTE n, const unsigned int l)
{
    encode(encoder, FNAME(golomb_code)(n, l), FNAME(golomb_code_len)(n, l));
}

static unsigned int FNAME(golomb_decoding)(const unsigned int l, const unsigned int bits,
                                           unsigned int * const codewordlen)
{
    if (bits > VNAME(family).notGRprefixmask[l]) { /*GR*/
        const unsigned int zeroprefix = cnt_l_zeroes(bits);       /* leading zeroes in codeword */
        const unsigned int cwlen = zeroprefix + 1 + l;            /* codeword length */
        (*codewordlen) = cwlen;
        return (zeroprefix << l) | ((bits >> (32 - cwlen)) & bppmask[l]);
    } else { /* not-GR */
        const unsigned int cwlen = VNAME(family).notGRcwlen[l];
        (*codewordlen) = cwlen;
        return VNAME(family).nGRcodewords[l] + ((bits) >> (32 - cwlen) &
                                                bppmask[VNAME(family).notGRsuffixlen[l]]);
    }
}

/* update the bucket using just encoded curval */
static void FNAME(update_model)(CommonState *state, s_bucket * const bucket,
                                const BYTE curval)
{
    SPICE_VERIFY(BPC >= 1);
    spice_return_if_fail (bucket != NULL);

    const unsigned int bpp = BPC;
    COUNTER * const pcounters = bucket->pcounters;
    unsigned int i;
    unsigned int bestcode;
    unsigned int bestcodelen;

    /* update counters, find minimum */

#if BPC == 8 && defined(__AVX2__)
    /* SPICESHARP MODIFICATION: the loop below, vectorized. A bucket has 8 counters (find_model_params) and
       a golomb_code_len row 8 entries. Adds wrap the same way, and the minimum is unsigned; the loop keeps
       the highest index among equal minima, which is the highest set bit of the equality mask. */
    {
        __m256i c = _mm256_add_epi32(_mm256_loadu_si256((const __m256i *)(const void *)pcounters),
                                     _mm256_loadu_si256((const __m256i *)(const void *)VNAME(family).golomb_code_len[curval]));
        _mm256_storeu_si256((__m256i *)(void *)pcounters, c);
        __m256i m = _mm256_min_epu32(c, _mm256_shuffle_epi32(c, _MM_SHUFFLE(2, 3, 0, 1)));
        m = _mm256_min_epu32(m, _mm256_shuffle_epi32(m, _MM_SHUFFLE(1, 0, 3, 2)));
        m = _mm256_min_epu32(m, _mm256_permute2x128_si256(m, m, 1));
        unsigned int mask = (unsigned int)_mm256_movemask_ps(_mm256_castsi256_ps(_mm256_cmpeq_epi32(c, m)));
        bestcode = 31 - (unsigned int)__builtin_clz(mask);
        bestcodelen = (unsigned int)_mm_cvtsi128_si32(_mm256_castsi256_si128(m));
        (void)i;
    }
#else
    bestcode = bpp - 1;
    bestcodelen = (pcounters[bestcode] += FNAME(golomb_code_len)(curval, bestcode));

    for (i = bpp - 2; i < bpp; i--) { /* NOTE: expression i<bpp for signed int i would be: i>=0 */
        const unsigned int ithcodelen = (pcounters[i] += FNAME(golomb_code_len)(curval, i));

        if (ithcodelen < bestcodelen) {
            bestcode = i;
            bestcodelen = ithcodelen;
        }
    }
#endif

    bucket->bestcode = bestcode; /* store the found minimum */

    if (bestcodelen > state->wm_trigger) { /* halving counters? */
        for (i = 0; i < bpp; i++) {
            pcounters[i] >>= 1;
        }
    }
}

static s_bucket *FNAME(find_bucket)(Channel *channel, const unsigned int val)
{
    spice_extra_assert(val < (0x1U << BPC));

    /* The and (&) here is to avoid buffer overflows in case of garbage or malicious
     * attempts. Is much faster then using comparisons and save us from such situations.
     * Note that on normal build the check above won't be compiled as this code path
     * is pretty hot and would cause speed regressions.
     */
    return channel->_buckets_ptrs[val & ((1U << BPC) - 1)];
}

#undef FNAME
#undef VNAME
#undef BPC
