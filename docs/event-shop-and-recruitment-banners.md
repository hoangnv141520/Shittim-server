# Event purchases and pickup banner images

Verified against the installed 1.93.453174 Steam client on 2026-10-30.

The client lists event 859 through `EventContent_ShopList` but buys product
8590116 through `Shop_BuyMerchandise`. The ordinary purchase handler previously
searched only `ShopExcel`, producing `Shop 8590116 not found` (error 10000).
It now resolves `EventContentShopExcel` products too, using the same conversion
as the event list. The requested goods must belong to the resolved product.
Purchase limits still use the existing persisted counters. Item-token changes
are returned through `ConsumeResultDB`, including depleted-stack removals.

The pickup artwork failure had a different cause. `UIGachaBannerTab` first
filters management banners by `BannerType = Gacha` and
`BannerDisplayType = Gacha`, then matches `LinkedLobbyBannerId` against
`ShopRecruitExcel.LinkedRobbyBannerId`. It downloads localized file names through
`GachaService.CoDownloadImage`. `GachaBannerPath` is only the asset fallback;
the shipped pickup rows leave it empty. The previous local art patch restored
that empty value after removing unrelated normal-recruitment art, while
`Management_BannerList` supplied no pickup banners.

`RecruitmentBannerService` now supplies active single-pickup rows when verified
art exists at `Data/Banner/recruitment-<character-id>.png`. Selection, normal,
multi-pickup, expired and unavailable-art rows are excluded. This is separate
from the Excel fallback repair and does not change recruitment schedules.

The following original PNGs were copied from this machine's game banner cache
after visually checking the character and outfit. They are English artwork,
also used as the fallback for other languages. Future pickups require their
own verified artwork; character ID prefixes are not an artwork mapping.

| Character ID | Pickup | Linked banner | Source cache filename |
| --- | --- | --- | --- |
| 10117 | Hikari | 725 | 3275753583 |
| 10118 | Nozomi | 726 | 2105282819 |
| 10101 | Saori (Swimsuit) | 727 | 3524650292 |
| 10102 | Hiyori (Swimsuit) | 728 | 4208065570 |
| 20049 | Misaki (Swimsuit) | 729 | 3670728550 |

`BannerController` resolves an exact filename before removing a recognized
language suffix. Previously an unlocalized `Event_Banner_854.png` was reduced
to `Event_Banner.png` and incorrectly fell back to Koyuki art.

Focused regressions exercise the real event product through the ordinary
purchase handler on a private SQLite database (remaining and depleted tokens,
rewards, purchase history and rejected foreign goods), and verify all five
active pickup links and localized image paths. Investigation evidence and
test reports live in the ignored `reports/event-shop-banner-2026-10-30/` folder.
