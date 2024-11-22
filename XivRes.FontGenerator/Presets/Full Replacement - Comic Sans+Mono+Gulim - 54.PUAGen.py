import dataclasses
import itertools
import math
import os.path
import re
import subprocess
import tempfile
import typing
import xml.dom.minidom

try:
	import fontforge
except e:
	fontforge = None
	print(e)


@dataclasses.dataclass(frozen=True)
class Vector2:
	ZERO: typing.ClassVar['Vector2']

	x: float
	y: float
	
	def __add__(self, right: 'Vector2'):
		return Vector2(self.x + right.x, self.y + right.y)
	
	def __sub__(self, right: 'Vector2'):
		return Vector2(self.x - right.x, self.y - right.y)
	
	def __repr__(self):
		return f"{self.x:g},{self.y:g}"
	
	def transform(self, by: 'Matrix3x2'):
		return Vector2(
			self.x * by.m11 + self.y * by.m21 + by.m31,
			self.x * by.m12 + self.y * by.m22 + by.m32);


Vector2.ZERO = Vector2(0., 0.)


@dataclasses.dataclass(frozen=True)
class Matrix3x2:
	IDENTITY: typing.ClassVar['Matrix3x2']

	# https://learn.microsoft.com/en-us/dotnet/api/system.numerics.matrix3x2?view=net-8.0
	m11: float
	m12: float
	m21: float
	m22: float
	m31: float
	m32: float
	
	def __mul__(value1, value2):
		return Matrix3x2(
			value1.m11 * value2.m11 + value1.m12 * value2.m21,
			value1.m11 * value2.m12 + value1.m12 * value2.m22,
			value1.m21 * value2.m11 + value1.m22 * value2.m21,
			value1.m21 * value2.m12 + value1.m22 * value2.m22,
			value1.m31 * value2.m11 + value1.m32 * value2.m21 + value2.m31,
			value1.m31 * value2.m12 + value1.m32 * value2.m22 + value2.m32)
	
	@staticmethod
	def create_translation(translation: Vector2):
		return Matrix3x2(1., 0., 0., 1., translation.x, translation.y)
	
	@staticmethod
	def create_scale(scale: Vector2, center: Vector2 = Vector2.ZERO):
		return Matrix3x2(scale.x, 0., 0., scale.y, center.x * (1. - scale.x), center.y * (1. - scale.y))
	
	@staticmethod
	def create_skew(rad_x: float, rad_y: float, center: Vector2 = Vector2.ZERO):
		x_tan = math.tan(rad_x)
		y_tan = math.tan(rad_y)

		tx = -center.y * x_tan;
		ty = -center.x * y_tan;

		return Matrix3x2(1., yTan, xTan, 1., tx, ty)


Matrix3x2.IDENTITY = Matrix3x2(1., 0., 0., 1., 0., 0.)


@dataclasses.dataclass(frozen=True)
class PathLine:
	p0: Vector2
	p1: Vector2
	
	def __str__(self):
		return f"L {self.p1}"
	
	@property
	def last(self) -> Vector2:
		return self.p1
	
	def reverse(self):
		return PathLine(self.p1, self.p0)
	
	def transform(self, by: Matrix3x2):
		return PathLine(self.p0.transform(by), self.p1.transform(by))


@dataclasses.dataclass(frozen=True)
class PathCubicBezierCurve:
	p0: Vector2
	p1: Vector2
	p2: Vector2
	p3: Vector2
	
	def __str__(self):
		return f"C {self.p1} {self.p2} {self.p3}"
	
	@property
	def last(self) -> Vector2:
		return self.p3
	
	def reverse(self):
		return PathCubicBezierCurve(self.p3, self.p2, self.p1, self.p0)
	
	def transform(self, by: Matrix3x2):
		return PathCubicBezierCurve(self.p0.transform(by), self.p1.transform(by), self.p2.transform(by), self.p3.transform(by))


@dataclasses.dataclass(frozen=True)
class PathQuadraticBezierCurve:
	p0: Vector2
	p1: Vector2
	p2: Vector2
	
	def __str__(self):
		return f"Q {self.p1} {self.p2}"
	
	@property
	def last(self) -> Vector2:
		return self.p2
	
	def reverse(self):
		return PathQuadraticBezierCurve(self.p2, self.p1, self.p0)
	
	def transform(self, by: Matrix3x2):
		return PathQuadraticBezierCurve(self.p0.transform(by), self.p1.transform(by), self.p2.transform(by))


@dataclasses.dataclass(frozen=True)
class PathArc:
	p0: Vector2
	p1: Vector2
	r: Vector2
	angle: float
	large_arc: bool
	sweep: bool
	
	def __str__(self):
		return f"A {self.radius} {self.angle} {'1' if self.large_arc else '0'} {'1' if self.sweep else '0'} {self.p1}"
	
	@property
	def last(self) -> Vector2:
		return self.p1
	
	def reverse(self):
		return PathArc(self.p1, self.p0, self.r, self.angle, self.large_arc, not self.sweep)
	
	def transform(self, by: Matrix3x2):
		# https://math.stackexchange.com/questions/4544164/transforming-ellipse-arc-start-and-end-angles-with-an-arbitrary-matrix
		raise NotImplementedError


class PathTokenizer:
	DEBUG = False
	
	def __init__(self, d: str):
		self.__d = d.replace(",", " ")
		self.__i = 0

		if PathTokenizer.DEBUG:
			print(self.__d)

	def __next__(self):
		while self.__i < len(self.__d) and (self.__d[self.__i].isspace()):
			self.__i += 1

		if self.__i >= len(self.__d):
			raise StopIteration
		first = self.__d[self.__i]
		first_i = self.__i
		self.__i += 1
		first_ord = ord(first)
		if first_ord >= ord('A') and first_ord <= ord('Z'):
			if PathTokenizer.DEBUG:
				print(first)
			return first
		if first_ord >= ord('a') and first_ord <= ord('z'):
			if PathTokenizer.DEBUG:
				print(first)
			return first
		
		while self.__i < len(self.__d):
			subsequent_ord = ord(self.__d[self.__i])
			if not ((subsequent_ord >= ord('0') and subsequent_ord <= ord('9')) or subsequent_ord == ord('.')):
				break
			self.__i += 1

		value = float(self.__d[first_i:self.__i])
		if PathTokenizer.DEBUG:
			print(value)
		return value


class SvgPath:
	def __init__(self, d: str | None = None):
		if d is None:
			self.__objects = []
			return
			
		first: Vector2 | None = None
		coord: Vector2 = Vector2(0, 0)
		objects = [[]]
		prevcmd = cmd = None

		tokenizer = PathTokenizer(d)
		# https://developer.mozilla.org/en-US/docs/Web/SVG/Attribute/d
		try:
			while True:
				try:
					what = next(tokenizer)
				except StopIteration:
					what = None

				if what == 'Z' or what == 'z':
					prevcmd = cmd = None
					if first is None:
						continue

					objects[-1].append(PathLine(coord, first))
					coord = first
					objects.append([])
					first = None
					if what is None:
						break
					continue

				if isinstance(what, str):
					cmd = what
					continue

				if what is None:
					break

				if cmd == 'M' or cmd == 'm':
					coord2 = Vector2(what, next(tokenizer))
					if cmd == 'm':
						coord2 += coord
					coord = coord2
					cmd = 'l' if cmd == 'm' else 'L'
				elif cmd == 'L' or cmd == 'l':
					coord2 = Vector2(what, next(tokenizer))
					if cmd == 'l':
						coord2 += coord
					objects[-1].append(PathLine(coord, coord2))
					coord = coord2
				elif cmd == 'H' or cmd == 'h':
					coord2 = Vector2(what, coord.y) if cmd == 'H' else Vector2(coord.x + what, coord.y)
					objects[-1].append(PathLine(coord, coord2))
					coord = coord2
				elif cmd == 'V' or cmd == 'v':
					coord2 = Vector2(coord.x, what) if cmd == 'V' else Vector2(coord.x, coord.y + what)
					objects[-1].append(PathLine(coord, coord2))
					coord = coord2
				elif cmd == 'C' or cmd == 'c':
					coord2 = Vector2(what, next(tokenizer))
					coord3 = Vector2(next(tokenizer), next(tokenizer))
					coord4 = Vector2(next(tokenizer), next(tokenizer))
					if cmd == 'c':
						coord2 += coord
						coord3 += coord
						coord4 += coord
					objects[-1].append(PathCubicBezierCurve(coord, coord2, coord3, coord4))
					coord = coord4
				elif cmd == 'S' or cmd == 's':
					if prevcmd == 'S' or prevcmd == 's' or prevcmd == 'C' or prevcmd == 'c':
						coord, coord2 = coord4, coord4 + (coord4 - coord3)
					else:
						coord2 = coord
					coord3 = Vector2(what, next(tokenizer))
					coord4 = Vector2(next(tokenizer), next(tokenizer))
					if cmd == 's':
						coord3 += coord
						coord4 += coord
					objects[-1].append(PathCubicBezierCurve(coord, coord2, coord3, coord4))
					coord = coord4
				elif cmd == 'Q' or cmd == 'q':
					coord2 = Vector2(what, next(tokenizer))
					coord3 = Vector2(next(tokenizer), next(tokenizer))
					if cmd == 'q':
						coord2 += coord
						coord3 += coord
					objects[-1].append(PathQuadraticBezierCurve(coord, coord2, coord3))
					coord = coord3
				elif cmd == 'T' or cmd == 't':
					if prevcmd == 'T' or prevcmd == 't' or prevcmd == 'Q' or prevcmd == 'q':
						coord, coord2 = coord3, coord3 + (coord3 - coord2)
					else:
						coord2 = coord
					coord3 = Vector2(what, next(tokenizer))
					if cmd == 't':
						coord3 += coord
					objects[-1].append(PathQuadraticBezierCurve(coord, coord2, coord3))
					coord = coord3
				elif cmd == 'A' or cmd == 'a':
					radius = Vector2(what, next(tokenizer))
					angle = next(tokenizer)
					large_arc_flag = next(tokenizer) != 0.
					sweep_flag = next(tokenizer) != 0.
					coord2 = Vector2(next(tokenizer), next(tokenizer))
					if cmd == 'a':
						coord2 += coord
					objects[-1].append(PathArc(coord, coord2, radius, angle, large_arc_flag, sweep_flag))
				elif cmd == 'Z' or cmd == 'z':
					pass
				else:
					print(cmd)
					raise NotImplementedError

				prevcmd = cmd
				if first is None:
					first = coord
		except StopIteration:
			pass
		
		objects.pop()
		self.__objects = objects
		if PathTokenizer.DEBUG:
			for object in objects:
				for item in object:
					print(item.p0, "->", item)
				print("-")
			print(self.to_svg_path_d())
	
	def append(self, other: 'SvgPath'):
		self.__objects.extend(other.__objects)
	
	def to_svg_path_d(self):
		strs = []
		for object in self.__objects:
			prev = None
			first = None
			for item in object:
				if prev is None or prev.x != item.p0.x or prev.y != item.p0.y:
					strs.append(f"M {item.p0}")
				if first is None:
					first = item.p0
					
				strs.append(str(item))
					
				prev = item.last
			strs.append("z")
		
		return " ".join(strs)
	
	def reverse(self):
		res = SvgPath()
		for object in reversed(self.__objects):
			res.__objects.append([])
			for item in reversed(object):
				res.__objects[-1].append(item.reverse())
		return res
	
	def transform(self, by: Matrix3x2):
		res = SvgPath()
		for object in self.__objects:
			res.__objects.append([])
			for item in object:
				res.__objects[-1].append(item.transform(by))
		return res


@dataclasses.dataclass(frozen=True)
class InverseBackground:
	left: int
	top: int
	right: int
	bottom: int
	path: SvgPath
	
	@property
	def box_width(self):
		return self.left + self.right
	
	@property
	def box_height(self):
		return self.top + self.bottom


@dataclasses.dataclass(frozen=True)
class GenDef:
	name: str
	items: list[str]
	first_codepoint: int
	background: InverseBackground
	transform: Matrix3x2 = Matrix3x2.IDENTITY
	
	@property
	def view_box(self):
		return f"{self.background.left} {self.background.top} {self.background.right} {self.background.bottom}"


inkscape = r"C:\Program Files\Inkscape\bin\inkscape.com"

round_box_hexagon = InverseBackground(-10, 0, 1260, 1000, SvgPath("M417 999h425q55 -1 110 -34q54 -33 82 -80l159 -279q26 -49 26 -111q0 -63 -26 -111l-159 -276q-28 -47 -82 -77 q-55 -30 -110 -31h-425q-55 1 -109 31q-55 30 -83 77l-159 275q-26 48 -26 111q0 64 26 112l159 278q28 48 83 81q54 33 109 34v0z"))
round_box_square = InverseBackground(-10, 0, 1010, 1000, SvgPath("M816 923q46 -1 77 -31q30 -30 32 -76v-328v-304q-2 -46 -32 -77q-31 -31 -77 -32h-632q-46 1 -77 32t-32 77v632q1 46 32 76t77 31h632z"))
round_box_square_wide = InverseBackground(-10, 0, 1760, 1000, SvgPath("M1382 50h-1014q-85 2 -142 59q-56 56 -58 141v500q2 85 58 141q57 56 142 58h1014q85 -2 142 -58q56 -56 58 -141v-500q-2 -85 -58 -141q-57 -57 -142 -59v0z"))
round_box_square_narrow = InverseBackground(-10, 0, 760, 1000, SvgPath("M151 917h438q47 -1 78 -31t32 -77v-638q-1 -46 -32 -78q-31 -31 -78 -32h-438q-47 1 -78 32q-31 32 -32 78v638q1 47 32 77t78 31v0z"))
round_box_square_wide2 = InverseBackground(-10, 0, 2010, 1000, SvgPath("M1703 968q99 -3 166 -69q66 -66 69 -165v-468q-3 -99 -69 -166q-67 -66 -166 -69h-1406q-99 3 -165 69q-66 67 -69 166v468q3 99 69 165t165 69h1406z"))
# <svg width="1000" height="1000" xmlns="http://www.w3.org/2000/svg">
round_box_square_outline = InverseBackground(-10, 0, 1010, 1000, SvgPath("M 190,45 C 110.10243,45 45,110.10243 45,190 v 620 c 0,79.89757 65.10243,145 145,145 h 620 c 79.89757,0 145,-65.10243 145,-145 V 190 C 955,110.10243 889.89757,45 810,45 Z m 0,50 h 620 c 53.06243,0 95,41.93757 95,95 v 620 c 0,53.06243 -41.93757,95 -95,95 H 190 C 136.93757,905 95,863.06243 95,810 V 190 c 0,-53.06243 41.93757,-95 95,-95 z").reverse())

gen_map: list[GenDef] = [
	GenDef(
		"Boxed A-Z (Filled)",
		[chr(x) for x in range(ord('A'), ord('Z') + 1)],
		0xE071,
		round_box_square),
	GenDef(
		"Boxed 0-9 (Filled)",
		[str(x) for x in range(0, 10)],
		0xE08F,
		round_box_square),
	GenDef(
		"Boxed 10-31 (Filled)",
		[str(x) for x in range(10, 32)],
		0xE099,
		round_box_square,
		Matrix3x2.create_scale(Vector2(0.7, 1.), Vector2(500, 500))),
	GenDef(
		"Boxed 0-9 (Outline)",
		[str(x) for x in range(0, 10)],
		0xE0E0,
		round_box_square_outline),
	GenDef(
		"Boxed Question Mark",
		["?"],
		0xE070,
		round_box_square),
	GenDef(
		"Boxed Plus",
		["+"],
		0xE0AF,
		round_box_square,
		Matrix3x2.create_scale(Vector2(2., 2.), Vector2(500, 500))
		* Matrix3x2.create_translation(Vector2(15, -130))),
	GenDef(
		"Boxed E",
		["E"],
		0xE0B0,
		round_box_square),
	GenDef(
		"Hexagonal Boxed 1-9 (Filled)",
		[str(x) for x in range(1, 10)],
		0xE0B1,
		round_box_hexagon,
		Matrix3x2.create_translation(Vector2(125, 0))),
	GenDef(
		"Bozja Rank I-VI",
		["I", "II", "III", "IV", "V", "VI"],
		0xE0C1,
		round_box_square_wide,
		Matrix3x2.create_translation(Vector2(375, 0))),
	GenDef(
		"Timezones",
		["LT", "ST", "ET", "DZ", "SZ", "EZ", "HL", "HS", "HE"],
		0xE0D0,
		round_box_square_wide2,
		Matrix3x2.create_translation(Vector2(500, 0))),
	GenDef(
		"Remaining Buff Timer (minutes)",
		["m"],
		0xE028,
		InverseBackground(-10, 0, 510, 1000, SvgPath("")),
		Matrix3x2.create_scale(Vector2(0.9, 1.), Vector2(500, 500))
		* Matrix3x2.create_translation(Vector2(-250, 40))),
	GenDef(
		"Level Text in Party List",
		["Lv"],
		0xE06A,
		InverseBackground(-10, 0, 1010, 1000, SvgPath("")),
		Matrix3x2.create_translation(Vector2(0, 90))),
	GenDef(
		"Level Text in Party List",
		["ST"],
		0xE06B,
		InverseBackground(-10, 0, 1110, 1000, SvgPath("")),
		Matrix3x2.create_translation(Vector2(0, 90))),
	GenDef(
		"Level Text in Party List",
		["Nv"],
		0xE06C,
		InverseBackground(-10, 0, 1010, 1000, SvgPath("")),
		Matrix3x2.create_translation(Vector2(0, 90))),
	GenDef(
		"Level Number in Party List",
		[str(x) for x in range(0, 10)],
		0xE060,
		InverseBackground(-10, 0, 410, 1000, SvgPath("")),
		Matrix3x2.create_translation(Vector2(-300, 90))),
	GenDef(
		"AM/PM",
		['<tspan x="0">A</tspan><tspan x="0" dy="0.8em">M</tspan>', '<tspan x="0">P</tspan><tspan x="0" dy="0.8em">M</tspan>'],
		0xE06D,
		round_box_square_narrow,
		Matrix3x2.create_scale(Vector2(0.8, 0.6), Vector2(500, 1000))
		* Matrix3x2.create_translation(Vector2(260, -420))),
]

texts = list(set(itertools.chain(*(x.items for x in gen_map))))


def __main__():
	font = fontforge.activeFont() if fontforge else None
	if font:
		font.fontname = "XIVPUAFromComic"
		font.fullname = font.familyname = "XIV PUA from Comic"
	paths = {}
	with tempfile.TemporaryDirectory() as td:
		temp_path = os.path.join(td, "tmp.svg")
		with open(temp_path, "w", encoding="utf-8") as fp:
			fp.write('<svg width="1000" height="1000" xmlns="http://www.w3.org/2000/svg">')
			for t in texts:
				if re.match('.*[0-9].*', t):
					family = "Comic Mono"
				else:
					family = "Comic Sans MS"
				fp.write(f'''<text
					x="500" y="580"
					font-family="{family}" font-weight="400" font-size="800"
					dominant-baseline="middle" text-anchor="middle">{t}</text>''')
			fp.write('</svg>')
		subprocess.call([
			inkscape,
			temp_path,
			"--actions=select-all:all;object-to-path",
			"--export-type=svg",
			"--export-filename=" + temp_path,
		])
		
		# print(open(temp_path).read())
		
		for path in xml.dom.minidom.parse(temp_path).getElementsByTagName("path"):
			svg_path = SvgPath(path.getAttribute("d"))
			label = path.getAttribute("aria-label")
			if not label:
				label = path.parentNode.getAttribute("aria-label")
			if label in paths:
				paths[label].append(svg_path)
			else:
				paths[label] = svg_path

		for gen_item in gen_map:
			codepoint = gen_item.first_codepoint
			for c in gen_item.items:
				filename = os.path.join(td, f"uni{codepoint:04X}-{codepoint}.svg")
				print(filename)
				with open(filename, "w", encoding="utf-8") as fp:
					fp.write('<?xml version="1.0" standalone="no"?>\n')
					fp.write('<!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">\n')
					fp.write(f'<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" version="1.1" viewBox="{gen_item.view_box}">\n')
					fp.write('<path fill="currentColor" d="')
					fp.write(gen_item.background.path.to_svg_path_d())
					if c != "":
						fp.write(" ")
						fp.write(paths[re.sub('<.*?>', '', c)].transform(gen_item.transform).to_svg_path_d())
					fp.write('" />\n')
					fp.write('</svg>\n')
				if font:
					glyph = font.createChar(codepoint)
					glyph.clear()
					glyph.width = gen_item.background.box_width
					glyph.vwidth = gen_item.background.box_height
					glyph.importOutlines(filename)
				codepoint += 1
	return 0


if __name__ == "__main__":
	__main__()
