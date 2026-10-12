"""Writes test PDFs with two independent PDF libraries (PyMuPDF and reportlab), so the app's
from-scratch PDF reader is checked against files it did not write itself.

Usage: python make_pdfs.py <output folder>
Each file is expected to contain the phrase "Govee H6056" except where noted in tests.
"""
import os
import sys

import fitz  # PyMuPDF
from reportlab.lib.pagesizes import letter
from reportlab.lib.styles import getSampleStyleSheet
from reportlab.pdfgen import canvas
from reportlab.platypus import Paragraph, SimpleDocTemplate

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
PHRASE = "Model: Govee H6056 Flow Plus light bars"


def mupdf(name, fontfile=None, pages=1, **save):
    doc = fitz.open()
    for i in range(pages):
        page = doc.new_page()
        text = PHRASE if i == pages - 1 else "Filler page %d with nothing to see." % (i + 1)
        if fontfile:
            page.insert_font(fontname="F0", fontfile=fontfile)
            page.insert_text((72, 100), text, fontname="F0", fontsize=12)
        else:
            page.insert_text((72, 100), text, fontname="helv", fontsize=12)
        page.insert_text((72, 130), "Second line of plain words.", fontname="helv", fontsize=11)
    doc.save(os.path.join(out, name), **save)


mupdf("mupdf_helv.pdf", deflate=True)
mupdf("mupdf_arial_type0.pdf", fontfile=r"C:\Windows\Fonts\arial.ttf", deflate=True)
mupdf("mupdf_page3.pdf", pages=3, deflate=True)
mupdf("mupdf_rc4.pdf", deflate=True, encryption=fitz.PDF_ENCRYPT_RC4_128, owner_pw="owner", user_pw="")
mupdf("mupdf_aes128.pdf", deflate=True, encryption=fitz.PDF_ENCRYPT_AES_128, owner_pw="owner", user_pw="")
mupdf("mupdf_aes256.pdf", deflate=True, encryption=fitz.PDF_ENCRYPT_AES_256, owner_pw="owner", user_pw="")
mupdf("locked_aes256.pdf", deflate=True, encryption=fitz.PDF_ENCRYPT_AES_256, owner_pw="owner", user_pw="secret")
try:
    mupdf("mupdf_objstm.pdf", deflate=True, use_objstms=1)
except TypeError:
    pass  # older PyMuPDF has no object-stream option

# reportlab: plain drawString, and a justified paragraph (word spacing set by the layout engine).
c = canvas.Canvas(os.path.join(out, "reportlab_canvas.pdf"), pagesize=letter)
c.setFont("Helvetica", 12)
c.drawString(72, 700, PHRASE)
c.drawString(72, 680, "Another line below it.")
c.showPage()
c.save()

style = getSampleStyleSheet()["BodyText"]
style.alignment = 4  # justified
words = ("The strip lights were installed along the cabinet and the " + PHRASE +
         " were mounted beside the television to see how they compare in a dark room. ") * 6
SimpleDocTemplate(os.path.join(out, "reportlab_justified.pdf"), pagesize=letter).build([Paragraph(words, style)])
print("ok")
