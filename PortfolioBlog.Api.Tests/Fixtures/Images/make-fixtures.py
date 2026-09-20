"""테스트 픽스처 이미지 생성 스크립트 (출처 기록용 — 테스트 실행에는 필요 없다).

필요: Python 3 + Pillow(PIL). 실행: python make-fixtures.py
결과물은 이미 저장소에 커밋돼 있다. 다시 만들면 바이트가 달라질 수 있으므로(Pillow 버전에 따라) 꼭 필요할 때만 실행한다.

각 파일에 심어 둔 "비밀"은 MetadataStripperTests가 제거 여부를 검사하는 표식이다.
  exif-gps.jpg             EXIF(Make=SpikeCam, DateTime, GPS 4개 태그) + COM("secret comment")
  exif-text.png            eXIf + tEXt(Author="secret author") + iTXt(Location="Seoul")
  exif-xmp.webp            EXIF 청크 + XMP 청크("<x:xmpmeta>secret...") + VP8X 플래그
  comment-animated.gif     주석 확장("secret gif comment") + NETSCAPE2.0 반복 확장, 2프레임
  progressive-trailing.jpg 프로그레시브 JPEG(스캔 여러 개, EXIF + COM) 뒤에 ZIP 시그니처와 "secret trailing payload"를 덧붙인 폴리글랏

계획 단계 검증(2026-09-21): 제거기를 적용한 결과를 Pillow로 다시 열어 (1) EXIF·GPS·텍스트·주석이 0건,
(2) 디코딩한 픽셀이 원본과 동일, (3) GIF 프레임 수 유지, (4) 재적용 시 바이트 동일, (5) 잘린 파일은 실패함을 확인했다.
"""
import os

from PIL import Image, PngImagePlugin

HERE = os.path.dirname(os.path.abspath(__file__))


def make_exif() -> Image.Exif:
    exif = Image.Exif()
    exif[0x010F] = "SpikeCam"             # Make
    exif[0x0132] = "2026:09:21 10:00:00"  # DateTime
    gps = exif.get_ifd(0x8825)
    gps[1] = "N"
    gps[2] = (37.0, 33.0, 59.0)
    gps[3] = "E"
    gps[4] = (126.0, 58.0, 40.0)
    return exif


def gradient(mode: str = "RGB", size: tuple[int, int] = (64, 48)) -> Image.Image:
    image = Image.new(mode, size)
    pixels = image.load()
    for y in range(size[1]):
        for x in range(size[0]):
            rgb = (x * 4 % 256, y * 5 % 256, (x + y) % 256)
            pixels[x, y] = rgb if mode == "RGB" else (*rgb, 200)
    return image


def main() -> None:
    exif = make_exif()
    gradient().save(os.path.join(HERE, "exif-gps.jpg"), quality=90, exif=exif, comment=b"secret comment")

    info = PngImagePlugin.PngInfo()
    info.add_text("Author", "secret author")
    info.add_itxt("Location", "Seoul")
    gradient("RGBA").save(os.path.join(HERE, "exif-text.png"), pnginfo=info, exif=exif)

    gradient("RGBA").save(os.path.join(HERE, "exif-xmp.webp"), lossless=True, exif=exif.tobytes(), xmp=b"<x:xmpmeta>secret</x:xmpmeta>")

    frames = [gradient().convert("P"), gradient().rotate(90, expand=False).convert("P")]
    frames[0].save(os.path.join(HERE, "comment-animated.gif"), save_all=True, append_images=frames[1:], loop=0, duration=80, comment=b"secret gif comment")

    progressive = os.path.join(HERE, "progressive-trailing.jpg")
    gradient(size=(200, 150)).save(progressive, quality=85, progressive=True, exif=exif, comment=b"secret comment")
    with open(progressive, "ab") as handle:
        handle.write(bytes([0x50, 0x4B, 0x03, 0x04]) + b" secret trailing payload")


if __name__ == "__main__":
    main()
