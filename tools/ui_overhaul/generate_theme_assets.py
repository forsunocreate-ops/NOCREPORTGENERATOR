import json
import os
from pathlib import Path

from materialyoucolor.dynamiccolor.material_dynamic_colors import MaterialDynamicColors as MDC
from materialyoucolor.hct import Hct
from materialyoucolor.scheme import SchemeVibrant
from coloraide import Color
from wcag_contrast_ratio import rgb as wcag_contrast
from PIL import Image, ImageDraw
from svgpathtools import svg2paths2
from scour import scour
from fontTools.ttLib import TTFont
import mss
import imagehash
import colour

try:
    import cairosvg
except Exception:
    cairosvg = None

try:
    from pywinauto import Desktop
except Exception:
    Desktop = None


def argb_to_hex(argb: int) -> str:
    return f"#{argb & 0xFFFFFFFF:08X}"


def color_to_argb_hex(color: Color) -> str:
    c = color.convert("srgb").fit(method="clip")
    r, g, b = [int(round(max(0.0, min(1.0, value)) * 255.0)) for value in c.coords()]
    return f"#FF{r:02X}{g:02X}{b:02X}"


def hex_to_rgb01(hex_color: str) -> tuple[float, float, float]:
    value = hex_color[-6:]
    return tuple(int(value[i : i + 2], 16) / 255.0 for i in (0, 2, 4))


def to_rgb_hex(hex_color: str) -> str:
    return "#" + hex_color[-6:]


def lighten(hex_color: str, amount: float) -> str:
    c = Color(to_rgb_hex(hex_color)).convert("oklch")
    c["l"] = min(1.0, c["l"] + amount)
    return color_to_argb_hex(c)


def darken(hex_color: str, amount: float) -> str:
    c = Color(to_rgb_hex(hex_color)).convert("oklch")
    c["l"] = max(0.0, c["l"] - amount)
    return color_to_argb_hex(c)


def mix(hex_a: str, hex_b: str, pct_b: float) -> str:
    c = Color(to_rgb_hex(hex_a)).mix(Color(to_rgb_hex(hex_b)), pct_b, space="oklch")
    return color_to_argb_hex(c)


def delta_e_2000(hex_a: str, hex_b: str) -> float:
    a = colour.XYZ_to_Lab(colour.sRGB_to_XYZ(hex_to_rgb01(hex_a)))
    b = colour.XYZ_to_Lab(colour.sRGB_to_XYZ(hex_to_rgb01(hex_b)))
    return float(colour.delta_E(a, b, method="CIE 2000"))


def dynamic_hex(scheme: SchemeVibrant, name: str) -> str:
    return argb_to_hex(getattr(MDC, name).get_argb(scheme))


def write_preview(preview_path: Path, palette: dict[str, str], contrast: dict[str, float]) -> str:
    width, height = 1600, 900
    img = Image.new("RGB", (width, height), tuple(int(palette["surface"][i : i + 2], 16) for i in (3, 5, 7)))
    draw = ImageDraw.Draw(img)

    hero_start = tuple(int(palette["hero_start"][i : i + 2], 16) for i in (3, 5, 7))
    hero_end = tuple(int(palette["hero_end"][i : i + 2], 16) for i in (3, 5, 7))
    for y in range(0, 240):
        t = y / 239
        row = (
            int(hero_start[0] * (1 - t) + hero_end[0] * t),
            int(hero_start[1] * (1 - t) + hero_end[1] * t),
            int(hero_start[2] * (1 - t) + hero_end[2] * t),
        )
        draw.line([(40, 40 + y), (1560, 40 + y)], fill=row, width=1)

    draw.rounded_rectangle((40, 40, 1560, 280), radius=36, outline=tuple(int(palette["hero_border"][i : i + 2], 16) for i in (3, 5, 7)), width=2)

    chip_colors = ["primary", "secondary", "tertiary", "success", "warning", "danger"]
    x = 56
    for key in chip_colors:
        fill = tuple(int(palette[key][i : i + 2], 16) for i in (3, 5, 7))
        draw.rounded_rectangle((x, 312, x + 230, 382), radius=20, fill=fill)
        draw.text((x + 14, 336), key.upper(), fill=(245, 250, 255))
        x += 252

    cards = [
        ("Surface", "surface"),
        ("Container", "surface_container"),
        ("ContainerHigh", "surface_container_high"),
        ("Outline", "outline"),
        ("NavSelected", "nav_selected_bg"),
    ]
    y = 430
    for label, key in cards:
        fill = tuple(int(palette[key][i : i + 2], 16) for i in (3, 5, 7))
        draw.rounded_rectangle((60, y, 760, y + 90), radius=22, fill=fill)
        draw.text((84, y + 30), f"{label}: {palette[key]}", fill=(20, 30, 45))
        y += 104

    draw.text((860, 430), "Contrast Audit", fill=(30, 40, 56))
    y = 470
    for key, value in contrast.items():
        draw.text((860, y), f"{key}: {value:.2f}", fill=(30, 40, 56))
        y += 34

    preview_path.parent.mkdir(parents=True, exist_ok=True)
    img.save(preview_path)
    return str(imagehash.phash(img))


def process_svg(svg_source: Path, output_svg: Path, output_png: Path) -> dict:
    output_svg.parent.mkdir(parents=True, exist_ok=True)
    raw_svg = svg_source.read_text(encoding="utf-8")
    options = scour.generateDefaultOptions()
    options.strip_comments = True
    options.remove_metadata = True
    options.enable_viewboxing = True
    options.shorten_ids = True
    options.indent_type = None
    options.newlines = False
    options = scour.sanitizeOptions(options)
    optimized_svg = scour.scourString(raw_svg, options)
    output_svg.write_text(optimized_svg, encoding="utf-8")

    paths, _, attrs = svg2paths2(str(output_svg))
    conversion_status = "ok"
    if cairosvg is not None:
        try:
            cairosvg.svg2png(bytestring=optimized_svg.encode("utf-8"), write_to=str(output_png), output_width=256, output_height=256)
        except Exception as exc:
            conversion_status = f"fallback_png: {exc}"
            Image.new("RGB", (256, 256), (232, 243, 246)).save(output_png)
    else:
        conversion_status = "fallback_png: cairosvg unavailable (missing cairo runtime)"
        Image.new("RGB", (256, 256), (232, 243, 246)).save(output_png)

    icon_hash = str(imagehash.phash(Image.open(output_png)))

    return {
        "path_count": len(paths),
        "element_count": len(attrs),
        "optimized_svg": str(output_svg),
        "preview_png": str(output_png),
        "icon_phash": icon_hash,
        "conversion_status": conversion_status,
    }


def capture_environment_screenshot(path: Path) -> str:
    path.parent.mkdir(parents=True, exist_ok=True)
    try:
        with mss.mss() as sct:
            monitor = sct.monitors[1]
            shot = sct.grab(monitor)
            img = Image.frombytes("RGB", shot.size, shot.rgb)
            img.save(path)
        return "ok"
    except Exception as exc:
        return f"skipped: {exc}"


def inspect_font(path: Path) -> dict:
    tt = TTFont(str(path))
    names = []
    for record in tt["name"].names:
        if record.nameID == 1:
            try:
                names.append(record.toUnicode())
            except Exception:
                pass
    tt.close()
    unique_names = sorted(set(names))
    return {
        "font_path": str(path),
        "family_names": unique_names[:8],
        "family_name_count": len(unique_names),
    }


def inspect_windows() -> list[str]:
    if Desktop is None:
        return ["pywinauto unavailable"]

    try:
        windows = Desktop(backend="uia").windows()
        titles = []
        for window in windows:
            text = (window.window_text() or "").strip()
            if text:
                titles.append(text)
        return titles[:15]
    except Exception as exc:
        return [f"window scan skipped: {exc}"]


def main() -> None:
    repo = Path(__file__).resolve().parents[2]
    docs_dir = repo / "NOCREPORTGENERATOR" / "Docs"
    ui_dir = repo / "NOCREPORTGENERATOR" / "Assets" / "UI"
    docs_dir.mkdir(parents=True, exist_ok=True)
    ui_dir.mkdir(parents=True, exist_ok=True)

    seed = "#0A7A6B"
    scheme = SchemeVibrant(Hct.from_int(int(seed.replace("#", "FF"), 16)), False, 0.35)

    base = {
        "primary": dynamic_hex(scheme, "primary"),
        "on_primary": dynamic_hex(scheme, "onPrimary"),
        "primary_container": dynamic_hex(scheme, "primaryContainer"),
        "on_primary_container": dynamic_hex(scheme, "onPrimaryContainer"),
        "secondary": dynamic_hex(scheme, "secondary"),
        "on_secondary": dynamic_hex(scheme, "onSecondary"),
        "secondary_container": dynamic_hex(scheme, "secondaryContainer"),
        "tertiary": dynamic_hex(scheme, "tertiary"),
        "surface": dynamic_hex(scheme, "surface"),
        "surface_container": dynamic_hex(scheme, "surfaceContainer"),
        "surface_container_high": dynamic_hex(scheme, "surfaceContainerHigh"),
        "outline": dynamic_hex(scheme, "outline"),
        "on_surface": dynamic_hex(scheme, "onSurface"),
    }

    palette = {
        **base,
        "hero_start": darken(base["primary"], 0.17),
        "hero_mid": mix(base["primary"], base["secondary"], 0.45),
        "hero_end": lighten(base["tertiary"], 0.04),
        "hero_border": lighten(base["secondary"], 0.08),
        "shell_background_start": lighten(base["surface"], 0.0),
        "shell_background_mid": lighten(base["surface_container"], 0.025),
        "shell_background_end": lighten(base["surface_container_high"], 0.05),
        "card_bg": "#FFFDFEFF",
        "card_bg_secondary": lighten(base["surface_container"], 0.04),
        "card_border": lighten(base["outline"], 0.12),
        "nav_selected_bg": lighten(base["primary_container"], 0.10),
        "nav_selected_border": base["primary"],
        "nav_default_bg": lighten(base["surface"], 0.02),
        "nav_default_border": lighten(base["outline"], 0.16),
        "title_fg": "#FF0E1A26",
        "muted_fg": "#FF4A6078",
        "success": "#FF1C8E5A",
        "warning": "#FFD88419",
        "danger": "#FFD64656",
        "import_bg": "#FFF4FBF9",
    }

    contrast = {
        "on_primary_vs_primary": wcag_contrast(hex_to_rgb01(base["on_primary"]), hex_to_rgb01(base["primary"])),
        "on_surface_vs_surface": wcag_contrast(hex_to_rgb01(base["on_surface"]), hex_to_rgb01(base["surface"])),
        "title_vs_card_bg": wcag_contrast(hex_to_rgb01(palette["title_fg"]), hex_to_rgb01(palette["card_bg"])),
    }

    colour_delta = {
        "delta_e_primary_secondary": delta_e_2000(base["primary"], base["secondary"]),
        "delta_e_primary_tertiary": delta_e_2000(base["primary"], base["tertiary"]),
    }

    preview_hash = write_preview(ui_dir / "theme-preview.png", palette, contrast)

    svg_result = process_svg(
        repo / "NOCREPORTGENERATOR" / "Assets" / "noc-icon-mono-nrg.svg",
        ui_dir / "noc-icon.optimized.svg",
        ui_dir / "noc-icon-preview.png",
    )

    screenshot_state = capture_environment_screenshot(ui_dir / "environment-capture.png")
    font_result = inspect_font(repo / "NOCREPORTGENERATOR" / "Assets" / "Fonts" / "remixicon.ttf")
    open_windows = inspect_windows()

    output = {
        "seed": seed,
        "palette": palette,
        "contrast": contrast,
        "contrast_pass": {k: v >= 4.5 for k, v in contrast.items()},
        "colour_delta": colour_delta,
        "preview_hash": preview_hash,
        "svg_result": svg_result,
        "font_result": font_result,
        "screenshot_state": screenshot_state,
        "open_windows_sample": open_windows,
    }

    json_path = docs_dir / "ui-theme.generated.json"
    json_path.write_text(json.dumps(output, indent=2), encoding="utf-8")

    md_path = docs_dir / "ui-theme.generated.md"
    md_path.write_text(
        "\n".join(
            [
                "# Generated UI Theme",
                "",
                f"Seed: `{seed}`",
                "",
                "## Contrast",
                f"- on_primary_vs_primary: `{contrast['on_primary_vs_primary']:.2f}`",
                f"- on_surface_vs_surface: `{contrast['on_surface_vs_surface']:.2f}`",
                f"- title_vs_card_bg: `{contrast['title_vs_card_bg']:.2f}`",
                "",
                "## Color Distance",
                f"- primary vs secondary (DeltaE2000): `{colour_delta['delta_e_primary_secondary']:.2f}`",
                f"- primary vs tertiary (DeltaE2000): `{colour_delta['delta_e_primary_tertiary']:.2f}`",
                "",
                "## Assets",
                f"- Preview: `{ui_dir / 'theme-preview.png'}`",
                f"- Optimized SVG: `{svg_result['optimized_svg']}`",
                f"- Icon PNG: `{svg_result['preview_png']}`",
                f"- Environment capture: `{ui_dir / 'environment-capture.png'}` ({screenshot_state})",
                "",
                "## Font",
                f"- Family count: `{font_result['family_name_count']}`",
                f"- Families: `{', '.join(font_result['family_names'])}`",
            ]
        ),
        encoding="utf-8",
    )

    print(f"Generated theme files at: {docs_dir}")


if __name__ == "__main__":
    main()
