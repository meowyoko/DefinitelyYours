# 包裹贴图

文件名固定为：

- `parcel_plain.png` / `parcel_plain_open.png`
- `parcel_gift.png` / `parcel_gift_open.png`
- `parcel_courier.png` / `parcel_courier_open.png`
- `parcel_crate.png` / `parcel_crate_open.png`
- `parcel_cute.png` / `parcel_cute_open.png`

贴图不存在时，mod 自动回退到原版 `eng_box` / `eng_box_u`，无需改配置。

当前规格均为 RGBA 透明底：

- `plain` / `gift` / `crate`：`64×48 px`，强制完整矩形 `4×3` 格。
- `courier` / `cute`：`48×48 px`，强制完整矩形 `3×3` 格。

关闭态和打开态必须尺寸一致。代码会在每次切换开合贴图后重新写入固定矩形形状，
不会按照两张图各自的透明区域重新计算占格。

来信册使用 `letters_book.png`：`32×32 px`、RGBA 透明底，外部占格强制完整矩形 `2×2`，
内部是 `6×8` 的实体容器，只接受 Definitely Yours 包裹里的字条。
