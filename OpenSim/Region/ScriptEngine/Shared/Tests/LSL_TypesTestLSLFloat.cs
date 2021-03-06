/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Collections.Generic;
using NUnit.Framework;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ScriptEngine.Shared.Tests
{
    [TestFixture]
    public class LSL_TypesTestLSLFloat : OpenSimTestCase
    {
        // Used for testing equality of two floats.
        private readonly double _lowPrecisionTolerance = 0.000001;

        private Dictionary<int, double> _intDoubleSet;
        private Dictionary<double, double> _doubleDoubleSet;
        private Dictionary<double, int> _doubleIntSet;
        private Dictionary<double, int> _doubleUintSet;
        private Dictionary<string, double> _stringDoubleSet;
        private Dictionary<double, string> _doubleStringSet;
        private List<int> _intList;
        private List<double> _doubleList;

        /// <summary>
        /// Sets up dictionaries and arrays used in the tests.
        /// </summary>
        [TestFixtureSetUp]
        public void SetUpDataSets()
        {
            _intDoubleSet = new Dictionary<int, double>();
            _intDoubleSet.Add(2, 2.0);
            _intDoubleSet.Add(-2, -2.0);
            _intDoubleSet.Add(0, 0.0);
            _intDoubleSet.Add(1, 1.0);
            _intDoubleSet.Add(-1, -1.0);
            _intDoubleSet.Add(999999999, 999999999.0);
            _intDoubleSet.Add(-99999999, -99999999.0);

            _doubleDoubleSet = new Dictionary<double, double>();
            _doubleDoubleSet.Add(2.0, 2.0);
            _doubleDoubleSet.Add(-2.0, -2.0);
            _doubleDoubleSet.Add(0.0, 0.0);
            _doubleDoubleSet.Add(1.0, 1.0);
            _doubleDoubleSet.Add(-1.0, -1.0);
            _doubleDoubleSet.Add(999999999.0, 999999999.0);
            _doubleDoubleSet.Add(-99999999.0, -99999999.0);
            _doubleDoubleSet.Add(0.5, 0.5);
            _doubleDoubleSet.Add(0.0005, 0.0005);
            _doubleDoubleSet.Add(0.6805, 0.6805);
            _doubleDoubleSet.Add(-0.5, -0.5);
            _doubleDoubleSet.Add(-0.0005, -0.0005);
            _doubleDoubleSet.Add(-0.6805, -0.6805);
            _doubleDoubleSet.Add(548.5, 548.5);
            _doubleDoubleSet.Add(2.0005, 2.0005);
            _doubleDoubleSet.Add(349485435.6805, 349485435.6805);
            _doubleDoubleSet.Add(-548.5, -548.5);
            _doubleDoubleSet.Add(-2.0005, -2.0005);
            _doubleDoubleSet.Add(-349485435.6805, -349485435.6805);

            _doubleIntSet = new Dictionary<double, int>();
            _doubleIntSet.Add(2.0, 2);
            _doubleIntSet.Add(-2.0, -2);
            _doubleIntSet.Add(0.0, 0);
            _doubleIntSet.Add(1.0, 1);
            _doubleIntSet.Add(-1.0, -1);
            _doubleIntSet.Add(999999999.0, 999999999);
            _doubleIntSet.Add(-99999999.0, -99999999);
            _doubleIntSet.Add(0.5, 0);
            _doubleIntSet.Add(0.0005, 0);
            _doubleIntSet.Add(0.6805, 0);
            _doubleIntSet.Add(-0.5, 0);
            _doubleIntSet.Add(-0.0005, 0);
            _doubleIntSet.Add(-0.6805, 0);
            _doubleIntSet.Add(548.5, 548);
            _doubleIntSet.Add(2.0005, 2);
            _doubleIntSet.Add(349485435.6805, 349485435);
            _doubleIntSet.Add(-548.5, -548);
            _doubleIntSet.Add(-2.0005, -2);
            _doubleIntSet.Add(-349485435.6805, -349485435);

            _doubleUintSet = new Dictionary<double, int>();
            _doubleUintSet.Add(2.0, 2);
            _doubleUintSet.Add(-2.0, 2);
            _doubleUintSet.Add(0.0, 0);
            _doubleUintSet.Add(1.0, 1);
            _doubleUintSet.Add(-1.0, 1);
            _doubleUintSet.Add(999999999.0, 999999999);
            _doubleUintSet.Add(-99999999.0, 99999999);
            _doubleUintSet.Add(0.5, 0);
            _doubleUintSet.Add(0.0005, 0);
            _doubleUintSet.Add(0.6805, 0);
            _doubleUintSet.Add(-0.5, 0);
            _doubleUintSet.Add(-0.0005, 0);
            _doubleUintSet.Add(-0.6805, 0);
            _doubleUintSet.Add(548.5, 548);
            _doubleUintSet.Add(2.0005, 2);
            _doubleUintSet.Add(349485435.6805, 349485435);
            _doubleUintSet.Add(-548.5, 548);
            _doubleUintSet.Add(-2.0005, 2);
            _doubleUintSet.Add(-349485435.6805, 349485435);

            _stringDoubleSet = new Dictionary<string, double>();
            _stringDoubleSet.Add("2", 2.0);
            _stringDoubleSet.Add("-2", -2.0);
            _stringDoubleSet.Add("1", 1.0);
            _stringDoubleSet.Add("-1", -1.0);
            _stringDoubleSet.Add("0", 0.0);
            _stringDoubleSet.Add("999999999.0", 999999999.0);
            _stringDoubleSet.Add("-99999999.0", -99999999.0);
            _stringDoubleSet.Add("0.5", 0.5);
            _stringDoubleSet.Add("0.0005", 0.0005);
            _stringDoubleSet.Add("0.6805", 0.6805);
            _stringDoubleSet.Add("-0.5", -0.5);
            _stringDoubleSet.Add("-0.0005", -0.0005);
            _stringDoubleSet.Add("-0.6805", -0.6805);
            _stringDoubleSet.Add("548.5", 548.5);
            _stringDoubleSet.Add("2.0005", 2.0005);
            _stringDoubleSet.Add("349485435.6805", 349485435.6805);
            _stringDoubleSet.Add("-548.5", -548.5);
            _stringDoubleSet.Add("-2.0005", -2.0005);
            _stringDoubleSet.Add("-349485435.6805", -349485435.6805);
            // some oddball combinations and exponents
            _stringDoubleSet.Add("", 0.0);
            _stringDoubleSet.Add("1.0E+5", 100000.0);
            _stringDoubleSet.Add("-1.0E+5", -100000.0);
            _stringDoubleSet.Add("-1E+5", -100000.0);
            _stringDoubleSet.Add("-1.E+5", -100000.0);
            _stringDoubleSet.Add("-1.E+5.0", -100000.0);
            _stringDoubleSet.Add("1ef", 1.0);
            _stringDoubleSet.Add("e10", 0.0);
            _stringDoubleSet.Add("1.e0.0", 1.0);

            _doubleStringSet = new Dictionary<double, string>();
            _doubleStringSet.Add(2.0, "2.000000");
            _doubleStringSet.Add(-2.0, "-2.000000");
            _doubleStringSet.Add(1.0, "1.000000");
            _doubleStringSet.Add(-1.0, "-1.000000");
            _doubleStringSet.Add(0.0, "0.000000");
            _doubleStringSet.Add(999999999.0, "999999999.000000");
            _doubleStringSet.Add(-99999999.0, "-99999999.000000");
            _doubleStringSet.Add(0.5, "0.500000");
            _doubleStringSet.Add(0.0005, "0.000500");
            _doubleStringSet.Add(0.6805, "0.680500");
            _doubleStringSet.Add(-0.5, "-0.500000");
            _doubleStringSet.Add(-0.0005, "-0.000500");
            _doubleStringSet.Add(-0.6805, "-0.680500");
            _doubleStringSet.Add(548.5, "548.500000");
            _doubleStringSet.Add(2.0005, "2.000500");
            _doubleStringSet.Add(349485435.6805, "349485435.680500");
            _doubleStringSet.Add(-548.5, "-548.500000");
            _doubleStringSet.Add(-2.0005, "-2.000500");
            _doubleStringSet.Add(-349485435.6805, "-349485435.680500");

            _doubleList = new List<double>();
            _doubleList.Add(2.0);
            _doubleList.Add(-2.0);
            _doubleList.Add(1.0);
            _doubleList.Add(-1.0);
            _doubleList.Add(999999999.0);
            _doubleList.Add(-99999999.0);
            _doubleList.Add(0.5);
            _doubleList.Add(0.0005);
            _doubleList.Add(0.6805);
            _doubleList.Add(-0.5);
            _doubleList.Add(-0.0005);
            _doubleList.Add(-0.6805);
            _doubleList.Add(548.5);
            _doubleList.Add(2.0005);
            _doubleList.Add(349485435.6805);
            _doubleList.Add(-548.5);
            _doubleList.Add(-2.0005);
            _doubleList.Add(-349485435.6805);

            _intList = new List<int>();
            _intList.Add(2);
            _intList.Add(-2);
            _intList.Add(0);
            _intList.Add(1);
            _intList.Add(-1);
            _intList.Add(999999999);
            _intList.Add(-99999999);
        }

        /// <summary>
        /// Tests constructing a LSLFloat from an integer.
        /// </summary>
        [Test]
        public void TestConstructFromInt()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testFloat;

            foreach (KeyValuePair<int, double> number in _intDoubleSet)
            {
                testFloat = new LSL_Types.LSLFloat(number.Key);
                Assert.That(testFloat.value, new DoubleToleranceConstraint(number.Value, _lowPrecisionTolerance));
            }
        }

        /// <summary>
        /// Tests constructing a LSLFloat from a double.
        /// </summary>
        [Test]
        public void TestConstructFromDouble()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testFloat;

            foreach (KeyValuePair<double, double> number in _doubleDoubleSet)
            {
                testFloat = new LSL_Types.LSLFloat(number.Key);
                Assert.That(testFloat.value, new DoubleToleranceConstraint(number.Value, _lowPrecisionTolerance));
            }
        }

        /// <summary>
        /// Tests LSLFloat is correctly cast explicitly to integer.
        /// </summary>
        [Test]
        public void TestExplicitCastLSLFloatToInt()
        {
            TestHelpers.InMethod();

            int testNumber;

            foreach (KeyValuePair<double, int> number in _doubleIntSet)
            {
                testNumber = (int) new LSL_Types.LSLFloat(number.Key);
                Assert.AreEqual(number.Value, testNumber, "Converting double " + number.Key + ", expecting int " + number.Value);
            }
        }

        /// <summary>
        /// Tests LSLFloat is correctly cast explicitly to unsigned integer.
        /// </summary>
        [Test]
        public void TestExplicitCastLSLFloatToUint()
        {
            TestHelpers.InMethod();

            uint testNumber;

            foreach (KeyValuePair<double, int> number in _doubleUintSet)
            {
                testNumber = (uint) new LSL_Types.LSLFloat(number.Key);
                Assert.AreEqual(number.Value, testNumber, "Converting double " + number.Key + ", expecting uint " + number.Value);
            }
        }

        /// <summary>
        /// Tests LSLFloat is correctly cast implicitly to Boolean if non-zero.
        /// </summary>
        [Test]
        public void TestImplicitCastLSLFloatToBooleanTrue()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testFloat;
            bool testBool;

            foreach (double number in _doubleList)
            {
                testFloat = new LSL_Types.LSLFloat(number);
                testBool = testFloat;

                Assert.IsTrue(testBool);
            }
        }

        /// <summary>
        /// Tests LSLFloat is correctly cast implicitly to Boolean if zero.
        /// </summary>
        [Test]
        public void TestImplicitCastLSLFloatToBooleanFalse()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testFloat = new LSL_Types.LSLFloat(0.0);
            bool testBool = testFloat;

            Assert.IsFalse(testBool);
        }

        /// <summary>
        /// Tests integer is correctly cast implicitly to LSLFloat.
        /// </summary>
        [Test]
        public void TestImplicitCastIntToLSLFloat()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testFloat;

            foreach (int number in _intList)
            {
                testFloat = number;
                Assert.That(testFloat.value, new DoubleToleranceConstraint(number, _lowPrecisionTolerance));
            }
        }

        /// <summary>
        /// Tests LSLInteger is correctly cast implicitly to LSLFloat.
        /// </summary>
        [Test]
        public void TestImplicitCastLSLIntegerToLSLFloat()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testFloat;

            foreach (int number in _intList)
            {
                testFloat = new LSL_Types.LSLInteger(number);
                Assert.That(testFloat.value, new DoubleToleranceConstraint(number, _lowPrecisionTolerance));
            }
        }

        /// <summary>
        /// Tests LSLInteger is correctly cast explicitly to LSLFloat.
        /// </summary>
        [Test]
        public void TestExplicitCastLSLIntegerToLSLFloat()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testFloat;

            foreach (int number in _intList)
            {
                testFloat = new LSL_Types.LSLInteger(number);
                Assert.That(testFloat.value, new DoubleToleranceConstraint(number, _lowPrecisionTolerance));
            }
        }

        /// <summary>
        /// Tests string is correctly cast explicitly to LSLFloat.
        /// </summary>
        [Test]
        public void TestExplicitCastStringToLSLFloat()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testFloat;

            foreach (KeyValuePair<string, double> number in _stringDoubleSet)
            {
                testFloat = (LSL_Types.LSLFloat) number.Key;
                Assert.That(testFloat.value, new DoubleToleranceConstraint(number.Value, _lowPrecisionTolerance));
            }
        }

        /// <summary>
        /// Tests LSLString is correctly cast implicitly to LSLFloat.
        /// </summary>
        [Test]
        public void TestExplicitCastLSLStringToLSLFloat()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testFloat;

            foreach (KeyValuePair<string, double> number in _stringDoubleSet)
            {
                testFloat = new LSL_Types.LSLString(number.Key);
                Assert.That(testFloat.value, new DoubleToleranceConstraint(number.Value, _lowPrecisionTolerance));
            }
        }

        /// <summary>
        /// Tests double is correctly cast implicitly to LSLFloat.
        /// </summary>
        [Test]
        public void TestImplicitCastDoubleToLSLFloat()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testFloat;

            foreach (double number in _doubleList)
            {
                testFloat = number;
                Assert.That(testFloat.value, new DoubleToleranceConstraint(number, _lowPrecisionTolerance));
            }
        }

        /// <summary>
        /// Tests LSLFloat is correctly cast implicitly to double.
        /// </summary>
        [Test]
        public void TestImplicitCastLSLFloatToDouble()
        {
            TestHelpers.InMethod();

            double testNumber;
            LSL_Types.LSLFloat testFloat;

            foreach (double number in _doubleList)
            {
                testFloat = new LSL_Types.LSLFloat(number);
                testNumber = testFloat;

                Assert.That(testNumber, new DoubleToleranceConstraint(number, _lowPrecisionTolerance));
            }
        }

        /// <summary>
        /// Tests LSLFloat is correctly cast explicitly to float
        /// </summary>
        [Test]
        public void TestExplicitCastLSLFloatToFloat()
        {
            TestHelpers.InMethod();

            float testFloat;
            float numberAsFloat;
            LSL_Types.LSLFloat testLSLFloat;

            foreach (double number in _doubleList)
            {
                testLSLFloat = new LSL_Types.LSLFloat(number);
                numberAsFloat = (float)number;
                testFloat = (float)testLSLFloat;

                Assert.That((double)testFloat, new DoubleToleranceConstraint(numberAsFloat, _lowPrecisionTolerance));
            }
        }

        /// <summary>
        /// Tests the equality (==) operator.
        /// </summary>
        [Test]
        public void TestEqualsOperator()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testFloatA, testFloatB;

            foreach (double number in _doubleList)
            {
                testFloatA = new LSL_Types.LSLFloat(number);
                testFloatB = new LSL_Types.LSLFloat(number);
                Assert.IsTrue(testFloatA == testFloatB);

                testFloatB = new LSL_Types.LSLFloat(number + 1.0);
                Assert.IsFalse(testFloatA == testFloatB);
            }
        }

        /// <summary>
        /// Tests the inequality (!=) operator.
        /// </summary>
        [Test]
        public void TestNotEqualOperator()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testFloatA, testFloatB;

            foreach (double number in _doubleList)
            {
                testFloatA = new LSL_Types.LSLFloat(number);
                testFloatB = new LSL_Types.LSLFloat(number + 1.0);
                Assert.IsTrue(testFloatA != testFloatB);

                testFloatB = new LSL_Types.LSLFloat(number);
                Assert.IsFalse(testFloatA != testFloatB);
            }
        }

        /// <summary>
        /// Tests the increment operator.
        /// </summary>
        [Test]
        public void TestIncrementOperator()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testFloat;
            double testNumber;

            foreach (double number in _doubleList)
            {
                testFloat = new LSL_Types.LSLFloat(number);

                testNumber = testFloat++;
                Assert.That(testNumber, new DoubleToleranceConstraint(number, _lowPrecisionTolerance));

                testNumber = testFloat;
                Assert.That(testNumber, new DoubleToleranceConstraint(number + 1.0, _lowPrecisionTolerance));

                testNumber = ++testFloat;
                Assert.That(testNumber, new DoubleToleranceConstraint(number + 2.0, _lowPrecisionTolerance));
            }
        }

        /// <summary>
        /// Tests the decrement operator.
        /// </summary>
        [Test]
        public void TestDecrementOperator()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testFloat;
            double testNumber;

            foreach (double number in _doubleList)
            {
                testFloat = new LSL_Types.LSLFloat(number);

                testNumber = testFloat--;
                Assert.That(testNumber, new DoubleToleranceConstraint(number, _lowPrecisionTolerance));

                testNumber = testFloat;
                Assert.That(testNumber, new DoubleToleranceConstraint(number - 1.0, _lowPrecisionTolerance));

                testNumber = --testFloat;
                Assert.That(testNumber, new DoubleToleranceConstraint(number - 2.0, _lowPrecisionTolerance));
            }
        }

        /// <summary>
        /// Tests LSLFloat.ToString().
        /// </summary>
        [Test]
        public void TestToString()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testFloat;

            foreach (KeyValuePair<double, string> number in _doubleStringSet)
            {
                testFloat = new LSL_Types.LSLFloat(number.Key);
                Assert.AreEqual(number.Value, testFloat.ToString());
            }
        }

        /// <summary>
        /// Tests addition of two LSLFloats.
        /// </summary>
        [Test]
        public void TestAddTwoLSLFloats()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testResult;

            foreach (KeyValuePair<double, double> number in _doubleDoubleSet)
            {
                testResult = new LSL_Types.LSLFloat(number.Key) + new LSL_Types.LSLFloat(number.Value);
                Assert.That(testResult.value, new DoubleToleranceConstraint(number.Key + number.Value, _lowPrecisionTolerance));
            }
        }

        /// <summary>
        /// Tests subtraction of two LSLFloats.
        /// </summary>
        [Test]
        public void TestSubtractTwoLSLFloats()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testResult;

            foreach (KeyValuePair<double, double> number in _doubleDoubleSet)
            {
                testResult = new LSL_Types.LSLFloat(number.Key) - new LSL_Types.LSLFloat(number.Value);
                Assert.That(testResult.value, new DoubleToleranceConstraint(number.Key - number.Value, _lowPrecisionTolerance));
            }
        }

        /// <summary>
        /// Tests multiplication of two LSLFloats.
        /// </summary>
        [Test]
        public void TestMultiplyTwoLSLFloats()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testResult;

            foreach (KeyValuePair<double, double> number in _doubleDoubleSet)
            {
                testResult = new LSL_Types.LSLFloat(number.Key) * new LSL_Types.LSLFloat(number.Value);
                Assert.That(testResult.value, new DoubleToleranceConstraint(number.Key * number.Value, _lowPrecisionTolerance));
            }
        }

        /// <summary>
        /// Tests division of two LSLFloats.
        /// </summary>
        [Test]
        public void TestDivideTwoLSLFloats()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testResult;

            foreach (KeyValuePair<double, double> number in _doubleDoubleSet)
            {
                if (number.Value != 0.0) // Let's avoid divide by zero.
                {
                    testResult = new LSL_Types.LSLFloat(number.Key) / new LSL_Types.LSLFloat(number.Value);
                    Assert.That(testResult.value, new DoubleToleranceConstraint(number.Key / number.Value, _lowPrecisionTolerance));
                }
            }
        }

        /// <summary>
        /// Tests boolean correctly cast implicitly to LSLFloat.
        /// </summary>
        [Test]
        public void TestImplicitCastBooleanToLSLFloat()
        {
            TestHelpers.InMethod();

            LSL_Types.LSLFloat testFloat;

            testFloat = 1 == 0;
            Assert.That(testFloat.value, new DoubleToleranceConstraint(0.0, _lowPrecisionTolerance));

            testFloat = 1 == 1;
            Assert.That(testFloat.value, new DoubleToleranceConstraint(1.0, _lowPrecisionTolerance));

            testFloat = false;
            Assert.That(testFloat.value, new DoubleToleranceConstraint(0.0, _lowPrecisionTolerance));

            testFloat = true;
            Assert.That(testFloat.value, new DoubleToleranceConstraint(1.0, _lowPrecisionTolerance));
        }
    }
}