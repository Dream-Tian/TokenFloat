from pathlib import Path
import sys

from PIL import Image


def create_icon(source_path: Path, output_directory: Path) -> None:
    """保留图片顶部主体，裁成正方形并生成应用 PNG 与多尺寸 ICO。"""
    with Image.open(source_path) as source:
        image = source.convert("RGB")
        side = min(image.width, image.height)
        left = (image.width - side) // 2
        square = image.crop((left, 0, left + side, side))
        icon_image = square.resize((512, 512), Image.Resampling.LANCZOS)

    output_directory.mkdir(parents=True, exist_ok=True)
    icon_image.save(output_directory / "tokenfloat-cat.png", optimize=True)
    icon_image.save(
        output_directory / "tokenfloat-cat.ico",
        format="ICO",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )


if __name__ == "__main__":
    create_icon(Path(sys.argv[1]), Path(sys.argv[2]))
