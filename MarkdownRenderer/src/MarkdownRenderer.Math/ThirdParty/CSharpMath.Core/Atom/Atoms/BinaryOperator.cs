namespace CSharpMath.Atom.Atoms {
  /// <summary>AMSMath class 2: Binary Operator (conjunction), e.g. +, \cup, \wedge</summary>
  internal sealed class BinaryOperator : MathAtom {
    public BinaryOperator(string nucleus) : base(nucleus) { }
    public override bool ScriptsAllowed => true;
    public new BinaryOperator Clone(bool finalize) => (BinaryOperator)base.Clone(finalize);
    protected override MathAtom CloneInside(bool finalize) => new BinaryOperator(Nucleus);
    public UnaryOperator ToUnaryOperator() =>
      ApplyCommonPropertiesOn(false, new UnaryOperator(Nucleus));
  }
}