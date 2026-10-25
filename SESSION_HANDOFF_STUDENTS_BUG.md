# BÁO CÁO TIẾN ĐỘ & HANDOFF: LỖI "ABNORMAL CLIENT" KHI VÀO MỤC STUDENTS

**Thời gian ghi nhận:** 2026-10-09  
**Tài khoản bị ảnh hưởng:** Account 2 (`Nanbaru Sensei`, `AccountServerId = 2`)  
**Tình trạng hiện tại:** Game hiển thị popup `"Notice: Abnormal client. Returning to the title screen."` khi nhấn vào nút **Students** (danh sách học sinh) từ Lobby.

---

## 1. Tóm tắt diễn biến & Hiện tượng (Timeline)
1. **Trước 03:11:**
   - Account 2 sở hữu 37 học sinh (ServerId từ 7 đến 47).
   - Người chơi vào mục Students, nâng level, xếp đội hình (Echelon), quét ải hoàn toàn bình thường (không có lỗi).
2. **Từ 03:11 đến 03:20:**
   - Người chơi thực hiện 20 lượt gacha x10 (`Shop_BuyGacha3`) trong Cash Shop.
   - Trúng thêm **6 học sinh mới** (được lưu vào bảng `Characters` với ServerId 48 đến 53):
     - **ServerId 48:** `UniqueId = 10014` (*Izuna_default*, SSR 3★, Release 2021)
     - **ServerId 49:** `UniqueId = 10107` (*CH0238 - Chiaki / Motomiya Chiaki*, SSR 3★, ReleaseDate 2025-05-13)
     - **ServerId 50:** `UniqueId = 23000` (*Airi_default*, 2★, Release 2021)
     - **ServerId 51:** `UniqueId = 10063` (*CH0198 - Koyuki*, SSR 3★, Release 2021/2023)
     - **ServerId 52:** `UniqueId = 13000` (*Akane_default*, 2★, Release 2021)
     - **ServerId 53:** `UniqueId = 10091` (*CH0250 - Kazusa Band*, SSR 3★, ReleaseDate 2024-10-22)
3. **Lúc 03:20:55:**
   - Người chơi rời Cash Shop về Lobby.
4. **Lúc 03:21:06:**
   - Người chơi bấm vào icon **Students**.
   - Không có gói tin nào gửi lên server (toàn bộ danh sách học sinh client lấy từ cache bộ nhớ `CharacterDBs` đã nhận lúc login).
   - Client ném unhandled exception ngầm bên trong Unity UI -> Trình bắt lỗi MX/CrashReporter hiển thị popup `"Abnormal client. Returning to the title screen."` và chuyển scene về màn hình Title.

---

## 2. Kết quả điều tra & Nguyên nhân cốt lõi

### A. Sự cố xảy ra hoàn toàn ở Client (Local Exception)
- `wire-2026-10-09.txt` chứng minh không có packet nào bị lỗi 500 hay timeout.
- `Player.log` ghi nhận `CrashReporterClient` kích hoạt cố gửi crash report về `localhost:80` (bị `Curl error 7` vì không có web server port 80), sau đó Unity dọn bộ nhớ và quay lại màn hình Title.

### B. Vấn đề của 6 học sinh mới trúng
- Trước khi gacha, màn hình Students hiển thị 37 học sinh sở hữu và các học sinh chưa sở hữu ở dạng silhouette/chưa mở khóa.
- Khi người chơi đã sở hữu (Owned = true), client chuyển sang chế độ hiển thị chi tiết (Avatar/Card, Weapon, Costume, Voice, Gear):
  - **Học sinh 10107 (Chiaki - CH0238):**
    - `ReleaseDate` và `CollectionVisibleStartDate` là `2025-05-13`.
    - Dù tên chuỗi thô có trong `catalog_Remote.bytes`, nhưng các asset bundle thực tế của Chiaki (animations, audio, Spine hoặc UI cell renderer) có thể không đầy đủ trong build Steam client này.
  - **Học sinh 10091 (Kazusa Band - CH0250):**
    - Có `CollectionVisibleEndDate = 2026-10-13 10:59:59` (thuộc banner giới hạn event).
  - So sánh với các học sinh trước đó (như CH0300 - CH0303) đã hoạt động bình thường vì chúng có đầy đủ prefab và asset tương thích.

---

## 3. Trạng thái hệ thống hiện tại
- **Backup database:** Đã tạo bản sao lưu an toàn tại:  
  `d:\server-ba\Shittim-Server\Shittim-Server\shittim.sqlite3.bak`
- **Mã nguồn kiểm thử:** Đã dọn sạch các hàm test tạm trong `d:\server-ba\Shittim-Server\Shittim-Server.Tests\MailCharacterStarTests.cs`.
- **Tiến trình Server:** `Shittim-Server` vẫn đang chạy ổn định ở background (`task-2584`) trên port 5000/5100.

---

## 4. Kế hoạch hành động cho Session tiếp theo (Next Steps)

1. **Bước 1: Cô lập & Xóa nhân vật lỗi trong DB**
   - Chạy lệnh SQL xóa học sinh tình nghi số 1 (Chiaki - 10107) khỏi Account 2:
     ```sql
     DELETE FROM Characters WHERE AccountServerId = 2 AND UniqueId = 10107;
     ```
   - Cho người chơi ấn Confirm trên game -> Đăng nhập lại -> Bấm vào mục **Students**.
   - Nếu vào được ngay => Xác nhận 100% Chiaki là nguyên nhân.
   - Nếu vẫn còn lỗi => Xóa tiếp học sinh tình nghi số 2 (Kazusa Band - 10091) hoặc xóa cả 6 học sinh gacha để game vào bình thường.

2. **Bước 2: Sửa logic Gacha Pool trong mã nguồn**
   - Mở file `Schale\Excel\CharacterExcelExt.cs` tại hàm `GetReleaseCharacters()`.
   - Bổ sung bộ lọc kiểm tra ngày ra mắt và tương thích client (tránh đưa các học sinh tương lai hoặc thiếu bundle vào pool gacha):
     ```csharp
     // Lọc bỏ các nhân vật chưa release hoặc chưa có bundle đầy đủ trong client build hiện tại
     ```
   - Rebuild lại server và kiểm tra unit tests.
